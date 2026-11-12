using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrowserDebugMCP;

/// <summary>
/// 独占一个 STA 线程来托管 WebView2。
///
/// 生命周期刻意分成两层，原因是一次实测踩坑：
/// - **MCP 服务进程常驻**。它一旦退出，Reasonix 会判定连接断开并自动重连，
///   也就是重新拉起进程 —— 用户会看到刚关掉的浏览器窗口又自己冒出来。
/// - **浏览器窗口按需创建**：进程启动时不建窗口；第一次工具调用才建；
///   用户把窗口关掉就彻底销毁它（释放 WebView2 资源），进程继续待命，下次调用再建。
///
/// 窗口内可以有多个标签，每个标签是一块**独立的 WebView2 实例**（页面与 JS 状态互不干扰），
/// 但同一时刻只展示一个：非活动标签用 Visibility.Collapsed 隐藏而不是移出视觉树，
/// 因为 HwndHost 一旦离开视觉树就会销毁原生窗口、连带使 CoreWebView2 失效。
///
/// MCP 工具没有"标签"参数：所有工具都作用在**活动标签**上。切到哪个标签，
/// open / evaluate / console / network 看到的就是哪个页面。
/// </summary>
internal sealed class WebViewHost : IBrowserHost, IBrowserWindowActions, IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private const int DefaultWindowWidth = 1280;
    private const int DefaultWindowHeight = 880;

    /// <summary>
    /// 默认页：首次建窗口的第一个标签、以及 `＋` / Ctrl+T 新建的标签都打开它。
    /// 工具带明确 URL 时不走这里（直接把目标 URL 当初始页，省一次导航）。
    /// </summary>
    private const string DefaultHomeUrl = "https://www.bing.com/";

    // 同一时刻只允许一次"启动浏览器"；并发调用共享同一次启动。
    private readonly SemaphoreSlim _browserGate = new(1, 1);

    private readonly string _userDataFolder;

    // 只在 UI 线程增删。
    private readonly List<BrowserTab> _tabs = new();

    private Dispatcher? _dispatcher;

    // 环境只建一次，所有标签共享同一份缓存与网络栈。
    private CoreWebView2Environment? _environment;
    private BrowserWindowUi? _ui;

    // 跨线程读（EnsureActiveTabAsync 在调用线程判断），仅在 UI 线程写。
    private volatile BrowserTab? _activeTab;

    private Window? _window;
    private Thread? _uiThread;

    // 只在 UI 线程读写。窗口被关掉重建时沿用最近一次 resize 的结果，
    // 否则尺寸会悄悄跳回默认值，调用方得再调一次才能恢复。
    private int _windowWidth = DefaultWindowWidth;
    private int _windowHeight = DefaultWindowHeight;

    public WebViewHost(string userDataFolder) => _userDataFolder = userDataFolder;

    /// <summary>启动常驻的 UI 线程。这时不创建浏览器 —— 窗口要等第一次工具调用。</summary>
    public void Start()
    {
        _uiThread = new Thread(UiThreadMain)
        {
            Name = "webview2-ui",
            IsBackground = true,
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    private void UiThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            // 兜底：UI 线程上任何一个未处理异常都会让 Dispatcher.Run 直接返回，
            // 线程一退出窗口就跟着消失 —— 表现为“浏览器自己关了”，而进程其实还活着。
            // 所以这里记日志并标记已处理，让消息循环继续跑下去。
            _dispatcher.UnhandledException += (_, e) =>
            {
                LogError("UI 线程未处理异常", e.Exception);
                e.Handled = true;
            };

            // 这里只跑消息循环。窗口按需创建，所以进程启动时不会弹任何窗口。
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            LogError("UI 线程启动", exception);
        }
    }

    /// <summary>活动标签可用就直接返回；窗口未建或已被关掉，则在 UI 线程上重建一个（含首个标签）。</summary>
    private async Task<BrowserTab> EnsureActiveTabAsync(CancellationToken cancellationToken) =>
        (await EnsureActiveTabCoreAsync(cancellationToken, initialUrl: null)).Tab;

    /// <summary>
    /// 同 <see cref="EnsureActiveTabAsync(CancellationToken)"/>，但可以指定"窗口刚建时首个标签打开哪个页面"。
    /// Created 表示这一轮是不是新建了窗口 —— 新建时已经导航过目标 URL，调用方可以据此省掉重复导航。
    /// </summary>
    private async Task<(BrowserTab Tab, bool Created)> EnsureActiveTabCoreAsync(
        CancellationToken cancellationToken,
        string? initialUrl)
    {
        if (_activeTab is { } existing) return (existing, false);

        await _browserGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeTab is { } created) return (created, false);

            var ready = new TaskCompletionSource<BrowserTab>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");

            // 默认页只在这里兜底；工具有明确目标 URL 时直接用目标 URL 当初始页。
            var url = string.IsNullOrWhiteSpace(initialUrl) ? DefaultHomeUrl : initialUrl;

            // 必须有窗口且消息循环已在运行才能初始化 WebView2，所以把委托排队到 UI 线程；
            // 此时 Dispatcher.Run() 已经在处理消息，满足 EnsureCoreWebView2Async 的前提。
            // 故意不等待这个排队操作本身：启动的成败通过 ready 回报。
            _ = dispatcher.InvokeAsync(() => _ = StartBrowserAsync(ready, url));

            return (await ready.Task.WaitAsync(cancellationToken), true);
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private async Task StartBrowserAsync(TaskCompletionSource<BrowserTab> ready, string initialUrl)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);

            if (_environment is null)
            {
                // browserExecutableFolder 传 null = 复用系统已安装的 Runtime（与 Edge 同版本）。
                _environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: _userDataFolder,
                    options: null);
            }

            var ui = new BrowserWindowUi(this, _windowWidth, _windowHeight);
            _ui = ui;
            _window = ui.Window;
            _window.Closed += (_, _) => OnBrowserWindowClosed(ui.Window);

            // 先 Show：EnsureCoreWebView2Async 要求应用消息循环已经在跑（README 坑 #7）。
            ui.Window.Show();

            var tab = await CreateTabAsync(initialUrl);
            ready.TrySetResult(tab);
        }
        catch (Exception exception)
        {
            LogError("WebView2 启动", exception);
            ready.TrySetException(exception);
        }
    }

    /// <summary>在 UI 线程创建一个标签并激活它；url 为 about:blank 时只开空白页不导航。</summary>
    private async Task<BrowserTab> CreateTabAsync(string url)
    {
        var ui = _ui ?? throw new InvalidOperationException("浏览器窗口尚未创建");
        var environment = _environment ?? throw new InvalidOperationException("WebView2 环境尚未就绪");

        var webView = new WebView2 { Visibility = Visibility.Collapsed };
        var tab = new BrowserTab(webView);

        ui.AttachContent(webView);

        await webView.EnsureCoreWebView2Async(environment);
        tab.Attach(webView.CoreWebView2);

        // 网页焦点下的 F5/F12 交给 WebView2 原生处理：WPF 版的 WebView2 控件没有
        // WinForms 那个 AcceleratorKeyPressed 事件，而网页是独立原生子窗口、
        // 键盘消息不会冒泡到 WPF 窗口。关掉浏览器快捷键反而两头都不响应，所以显式保持开启。
        tab.Core.Settings.AreBrowserAcceleratorKeysEnabled = true;

        tab.StateChanged += OnTabStateChanged;
        tab.NewWindowRequested += OnTabNewWindowRequested;

        _tabs.Add(tab);
        await EnableDomainsAsync(tab);

        ActivateTabCore(tab);

        if (!string.IsNullOrWhiteSpace(url) && !IsBlankUrl(url))
        {
            try
            {
                await NavigateTabAsync(tab, url, CancellationToken.None);
            }
            catch (Exception exception)
            {
                // 新标签打不开不该连累整个浏览器：留着这个标签，用户可在地址栏重试。
                LogError($"新标签导航到 {url}", exception);
            }
        }

        return tab;
    }

    private static async Task EnableDomainsAsync(BrowserTab tab)
    {
        // 事件只有在对应 domain enable 之后才会到达，顺序不能反。
        await tab.Core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await tab.Core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        await tab.Core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        await tab.Core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
    }

    /// <summary>切换活动标签：只改可见性，不动视觉树归属（见 BrowserTab 的说明）。</summary>
    private void ActivateTabCore(BrowserTab tab)
    {
        if (!_tabs.Contains(tab)) return;

        foreach (var candidate in _tabs)
        {
            candidate.WebView.Visibility = ReferenceEquals(candidate, tab)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        _activeTab = tab;
        tab.WebView.InvalidateVisual();
        RefreshChrome();

        // 让键盘输入直接进入网页（否则 F5 之类还得再点一下页面）。
        tab.WebView.Focus();
    }

    private void CloseTabCore(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;

        _tabs.RemoveAt(index);
        tab.StateChanged -= OnTabStateChanged;
        tab.NewWindowRequested -= OnTabNewWindowRequested;

        _ui?.DetachContent(tab.WebView);

        var wasActive = ReferenceEquals(_activeTab, tab);
        if (wasActive) _activeTab = null;
        tab.Dispose();

        if (_tabs.Count == 0)
        {
            // 关掉最后一个标签 = 关掉整个浏览器窗口（与真实浏览器一致）。
            _window?.Close();
            return;
        }

        if (wasActive) ActivateTabCore(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else RefreshChrome();
    }

    private void RefreshChrome()
    {
        if (_ui is null) return;

        _ui.RenderTabStrip(_tabs, _activeTab);

        if (_activeTab is { } tab)
        {
            _ui.SetAddress(tab.Url);
            _ui.SetNavigationState(tab.CanGoBack, tab.CanGoForward);
            if (_window is not null) _window.Title = $"BrowserDebugMCP — {tab.Title}";
        }
    }

    private void OnTabStateChanged(BrowserTab tab) => RefreshChrome();

    private void OnTabNewWindowRequested(BrowserTab source, string url) =>
        FireAndForget(() => CreateTabAsync(url), "页面请求打开新窗口");

    /// <summary>窗口被关掉：销毁所有标签，进程继续常驻等下一次调用。</summary>
    private void OnBrowserWindowClosed(Window window)
    {
        if (!ReferenceEquals(_window, window)) return;

        _window = null;
        _ui = null;
        _activeTab = null;

        foreach (var tab in _tabs.ToArray())
        {
            tab.StateChanged -= OnTabStateChanged;
            tab.NewWindowRequested -= OnTabNewWindowRequested;
            tab.Dispose();
        }

        _tabs.Clear();
    }

    /// <summary>显式关闭浏览器窗口（供 close_window 工具使用）。返回它此前是否开着。</summary>
    public async Task<bool> CloseBrowserAsync(CancellationToken cancellationToken = default)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null) return false;

        return await dispatcher.InvokeAsync(() =>
        {
            var window = _window;
            if (window is null) return false;
            window.Close();
            return true;
        });
    }

    /// <summary>
    /// 调整浏览器窗口尺寸（WPF DIP，含窗口边框与顶部工具栏）。参数为 null 表示沿用当前值；
    /// 两者都为 null 时只回报当前尺寸。窗口尚未创建时会先建（与其他工具一致）。
    /// </summary>
    public async Task<(int Width, int Height)> ResizeBrowserAsync(
        int? width,
        int? height,
        CancellationToken cancellationToken = default)
    {
        await EnsureActiveTabAsync(cancellationToken);

        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");

        return await dispatcher.InvokeAsync(() =>
        {
            var window = _window ?? throw new InvalidOperationException("浏览器窗口尚未就绪（可能刚被关闭），请重试");

            if (width is not null || height is not null)
            {
                if (width is { } newWidth) _windowWidth = newWidth;
                if (height is { } newHeight) _windowHeight = newHeight;

                // 最大化时设置 Width/Height 不生效，先还原成普通窗口，尺寸才真的会变。
                if (window.WindowState == WindowState.Maximized) window.WindowState = WindowState.Normal;

                window.Width = _windowWidth;
                window.Height = _windowHeight;
                window.UpdateLayout();
            }

            return (_windowWidth, _windowHeight);
        });
    }

    /// <summary>
    /// 抓**客户区完整画面**：WPF 的标签条/工具栏 + 活动标签里的网页，拼成一张图（物理像素）。
    /// 与 CDP 的 Page.captureScreenshot 不同：这条路径带 UI，能反映窗口被遮挡、缩放后的实际样子，
    /// 但要求窗口未被最小化。只想看页面本身用 screenshot（走 CDP，交给页面视口渲染帧）。
    /// </summary>
    public async Task<WindowCapture.Result> CaptureWindowAsync(CancellationToken cancellationToken = default)
    {
        await EnsureActiveTabAsync(cancellationToken);

        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");

        // 必须在 UI 线程调用：PrintWindow 会同步给目标窗口发 WM_PRINT，
        // 跨线程发同步消息容易把两个线程一起卡住。
        return await dispatcher.InvokeAsync(
            () =>
            {
                var window = _window ?? throw new InvalidOperationException("浏览器窗口尚未就绪（可能刚被关闭），请重试");
                var tab = _activeTab ?? throw new InvalidOperationException("活动标签尚未就绪，请重试");

                if (window.WindowState == WindowState.Minimized)
                    throw new InvalidOperationException("窗口已最小化，截不到画面；请先恢复窗口再调用");

                // chrome 走 WPF 渲染、网页走 PrintWindow，再拼起来（见 WindowCapture.CaptureComposite）：
                // 顶层窗口的 PrintWindow 拿不到 WPF 用 D3D 画出来的标签条/工具栏。
                var root = window.Content as FrameworkElement
                    ?? throw new InvalidOperationException("窗口内容不是可视元素，无法合成截图");

                window.UpdateLayout();
                return WindowCapture.CaptureComposite(root, tab.WebView, tab.WebView.Handle);
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    /// <summary>导航活动标签并等待 load 事件（或超时）。</summary>
    public async Task<string> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        // 窗口还没建时直接把目标 URL 当初始页：CreateTabAsync 已经等过 load 事件，
        // 这里就不必再导航一次（否则会先开默认页再跳走，白等一轮）。
        var (tab, created) = await EnsureActiveTabCoreAsync(cancellationToken, url);
        if (created && SameUrl(tab.Url, url)) return url;

        return await NavigateTabAsync(tab, url, cancellationToken);
    }

    private static bool SameUrl(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var a)
        && Uri.TryCreate(right, UriKind.Absolute, out var b)
        && a.Equals(b);

    private async Task<string> NavigateTabAsync(BrowserTab tab, string url, CancellationToken cancellationToken)
    {
        var load = tab.BeginLoadWait();

        await CallTabCdpAsync(tab, "Page.navigate", JsonParameters(new { url }));

        var finished = await Task.WhenAny(load.Task, Task.Delay(LoadTimeout, cancellationToken));
        if (finished == load.Task) return url;

        // 只有确实还是自己挂的那个等待时才清理，避免误伤后续导航。
        tab.AbandonLoadWait(load);
        return $"{url}（已发出导航，但 {LoadTimeout.TotalSeconds:0} 秒内没有收到 load 事件；页面可能仍在加载，可用 console 或 evaluate 继续观察）";
    }

    /// <summary>重新加载活动标签并等待 load 事件。</summary>
    public async Task<string> ReloadAsync(bool ignoreCache, CancellationToken cancellationToken = default)
    {
        var tab = await EnsureActiveTabAsync(cancellationToken);

        var load = tab.BeginLoadWait();

        await CallTabCdpAsync(tab, "Page.reload", JsonParameters(new { ignoreCache }));

        var finished = await Task.WhenAny(load.Task, Task.Delay(LoadTimeout, cancellationToken));
        return finished == load.Task ? "已重新加载" : "已发出重新加载，但未在超时内收到 load 事件";
    }

    /// <summary>调用任意 DevTools Protocol 方法（作用于活动标签），返回原始 JSON 响应。</summary>
    public async Task<string> CdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default)
    {
        var tab = await EnsureActiveTabAsync(cancellationToken);
        return await AwaitWithTimeout(CallTabCdpAsync(tab, method, parametersJson), CallTimeout, method);
    }

    /// <summary>读活动标签累积的 console/异常记录；代理会话经管道转发到这里。</summary>
    public async Task<string> ReadConsoleAsync(bool clear, CancellationToken cancellationToken = default)
    {
        var tab = await EnsureActiveTabAsync(cancellationToken);
        return tab.Console.Render(clear);
    }

    /// <summary>读活动标签累积的网络记录；代理会话经管道转发到这里。</summary>
    public async Task<string> ReadNetworkAsync(bool clear, CancellationToken cancellationToken = default)
    {
        var tab = await EnsureActiveTabAsync(cancellationToken);
        return tab.Network.Render(clear);
    }

    private Task<string> CallTabCdpAsync(BrowserTab tab, string method, string parametersJson)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");
        return dispatcher
            .InvokeAsync(() =>
            {
                var core = tab.Core ?? throw new InvalidOperationException("标签页尚未就绪");
                return core.CallDevToolsProtocolMethodAsync(method, parametersJson);
            })
            .Task
            .Unwrap();
    }

    private static async Task<string> AwaitWithTimeout(Task<string> call, TimeSpan timeout, string what)
    {
        var finished = await Task.WhenAny(call, Task.Delay(timeout));
        if (finished != call) throw new TimeoutException($"CDP 调用 {what} 超过 {timeout.TotalSeconds:0} 秒未返回");
        return await call;
    }

    private static string JsonParameters<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>把失败原因写到 stderr；stdout 是 MCP 协议通道，绝不能碰。</summary>
    private static void LogError(string stage, Exception exception)
    {
        Console.Error.WriteLine($"[BrowserDebugMCP] {stage} 失败：{exception}");
        Console.Error.Flush();
    }

    /// <summary>UI 触发的异步动作（导航、刷新、开标签）：失败只记日志，不能把窗口打崩。</summary>
    private static void FireAndForget(Func<Task> action, string what)
    {
        _ = RunAsync(action, what);

        static async Task RunAsync(Func<Task> action, string what)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                LogError(what, exception);
            }
        }
    }

    private static bool IsBlankUrl(string url) =>
        string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 地址栏输入归一化：补协议头。本机调试占绝大多数，所以 localhost / IP 走 http，
    /// 其余补 https；已有协议（含 about:/data:/file: 等）原样保留。
    /// </summary>
    private static string NormalizeAddress(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "about:blank";
        if (trimmed.Contains("://", StringComparison.Ordinal)) return trimmed;

        foreach (var scheme in new[] { "about:", "data:", "file:", "javascript:", "mailto:" })
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return trimmed;
        }

        var host = trimmed.Split('/', 2)[0].Split(':', 2)[0];
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _))
            return "http://" + trimmed;

        return "https://" + trimmed;
    }

    string IBrowserWindowActions.ActiveUrl => _activeTab?.Url ?? "about:blank";

    void IBrowserWindowActions.ActivateTab(BrowserTab tab) => ActivateTabCore(tab);

    void IBrowserWindowActions.CloseTab(BrowserTab tab) => CloseTabCore(tab);

    void IBrowserWindowActions.CloseActiveTab()
    {
        if (_activeTab is { } tab) CloseTabCore(tab);
    }

    void IBrowserWindowActions.OpenNewTab() =>
        FireAndForget(() => CreateTabAsync(DefaultHomeUrl), "新建标签页");

    void IBrowserWindowActions.NavigateFromAddressBar(string text)
    {
        var url = NormalizeAddress(text);
        FireAndForget(() => NavigateAsync(url), "地址栏导航");
    }

    void IBrowserWindowActions.GoBack()
    {
        if (_activeTab?.Core is { CanGoBack: true } core) core.GoBack();
    }

    void IBrowserWindowActions.GoForward()
    {
        if (_activeTab?.Core is { CanGoForward: true } core) core.GoForward();
    }

    void IBrowserWindowActions.ReloadActiveTab(bool ignoreCache) =>
        FireAndForget(() => ReloadAsync(ignoreCache), "刷新页面");

    void IBrowserWindowActions.OpenDevTools()
    {
        var core = _activeTab?.Core;
        if (core is null) return;

        try
        {
            core.OpenDevToolsWindow();
        }
        catch (Exception exception)
        {
            LogError("打开开发工具", exception);
        }
    }

    void IBrowserWindowActions.FocusActivePage() => _activeTab?.WebView.Focus();

    public void Dispose()
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
            {
                dispatcher.Invoke(() =>
                {
                    _window?.Close();
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                });
            }
        }
        catch
        {
            // 关闭阶段的异常无需上报
        }
    }
}
