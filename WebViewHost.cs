using System.IO;
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
/// </summary>
internal sealed class WebViewHost : IBrowserHost, IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private const int DefaultWindowWidth = 1280;
    private const int DefaultWindowHeight = 880;

    // 同一时刻只允许一次"启动浏览器"；并发调用共享同一次启动。
    private readonly SemaphoreSlim _browserGate = new(1, 1);

    // 必须长期持有 receiver 的引用：如果只是内联订阅，它会被 GC 回收，
    // 之后原生层触发回调时会造成随机内存访问违例。
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _receivers = new();

    private readonly string _userDataFolder;
    private readonly ConsoleBuffer _console;
    private readonly NetworkBuffer _network;

    private Dispatcher? _dispatcher;
    private CoreWebView2? _core;
    private WebView2? _webView;
    private Window? _window;
    private Thread? _uiThread;

    // 只在 UI 线程读写。窗口被关掉重建时沿用最近一次 resize 的结果，
    // 否则尺寸会悄悄跳回默认值，调用方得再调一次才能恢复。
    private int _windowWidth = DefaultWindowWidth;
    private int _windowHeight = DefaultWindowHeight;

    // 由 Page.loadEventFired 推动；导航前先挂上等待，避免"事件先于等待到达"的竞态。
    private TaskCompletionSource? _pendingLoad;

    public WebViewHost(string userDataFolder, ConsoleBuffer console, NetworkBuffer network)
    {
        _userDataFolder = userDataFolder;
        _console = console;
        _network = network;
    }

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

            // 这里只跑消息循环。窗口按需创建，所以进程启动时不会弹任何窗口。
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            LogError("UI 线程启动", exception);
        }
    }

    /// <summary>浏览器已可用就直接返回；尚未创建或已被关掉，则在 UI 线程上重建一个。</summary>
    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_core is not null) return;

        await _browserGate.WaitAsync(cancellationToken);
        try
        {
            if (_core is not null) return;

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");

            // 必须有窗口且消息循环已在运行才能初始化 WebView2，所以把委托排队到 UI 线程；
            // 此时 Dispatcher.Run() 已经在处理消息，满足 EnsureCoreWebView2Async 的前提。
            // 故意不等待这个排队操作本身：启动的成败通过 ready 回报。
            _ = dispatcher.InvokeAsync(() => _ = StartBrowserAsync(ready));

            await ready.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private async Task StartBrowserAsync(TaskCompletionSource ready)
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);

            var webView = new WebView2();
            var window = new Window
            {
                Title = "BrowserDebugMCP — 正在初始化浏览器",
                Width = _windowWidth,
                Height = _windowHeight,
                Content = webView,
            };
            window.Closed += (_, _) => OnBrowserWindowClosed(window);
            _window = window;
            _webView = webView;
            window.Show();

            // 显式指定 user data 目录：否则 WebView2 会用默认的 "<exe>.WebView2"。
            // browserExecutableFolder 传 null = 复用系统已安装的 Runtime（与 Edge 同版本）。
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: _userDataFolder,
                options: null);

            await webView.EnsureCoreWebView2Async(environment);
            _core = webView.CoreWebView2;

            // 重建时必须丢掉上一轮的 receiver，它们绑在已经销毁的核心对象上。
            _receivers.Clear();
            SubscribeProtocolEvents();

            // 事件只有在对应 domain enable 之后才会到达，顺序不能反。
            await CallCoreAsync("Runtime.enable", "{}");
            await CallCoreAsync("Log.enable", "{}");
            await CallCoreAsync("Page.enable", "{}");
            await CallCoreAsync("Network.enable", "{}");

            window.Title = "BrowserDebugMCP — about:blank";
            ready.TrySetResult();
        }
        catch (Exception exception)
        {
            LogError("WebView2 启动", exception);
            ready.TrySetException(exception);
        }
    }

    /// <summary>窗口被关掉：只销毁浏览器，进程继续常驻等下一次调用。</summary>
    private void OnBrowserWindowClosed(Window window)
    {
        if (!ReferenceEquals(_window, window)) return;

        _window = null;
        _webView = null;
        _core = null;
        _receivers.Clear();

        // 让挂起的导航等待立刻结束，而不是干等到超时。
        Interlocked.Exchange(ref _pendingLoad, null)?.TrySetResult();
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
    /// 调整浏览器窗口尺寸（WPF DIP，含窗口边框）。参数为 null 表示沿用当前值；
    /// 两者都为 null 时只回报当前尺寸。窗口尚未创建时会先建（与其他工具一致）。
    /// </summary>
    public async Task<(int Width, int Height)> ResizeBrowserAsync(
        int? width,
        int? height,
        CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(cancellationToken);

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
    /// 用 Win32 抓 WebView2 控件窗口的真实像素（PrintWindow，失败时 BitBlt 屏幕兜底）。
    /// 与 CDP 的 Page.captureScreenshot 不同：这条路径拿的是窗口像素，能反映窗口被遮挡、
    /// 缩放后的实际样子，但要求窗口未被最小化或隐藏。
    /// 注意：cancellationToken 只在排队到 UI 线程之前生效；委托一旦开始执行就会跑完。
    /// </summary>
    public async Task<WindowCapture.Result> CaptureWindowAsync(CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(cancellationToken);

        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");

        // 必须在 UI 线程调用：PrintWindow 会同步给目标窗口发 WM_PRINT，
        // 跨线程发同步消息容易把两个线程一起卡住。
        return await dispatcher.InvokeAsync(
            () =>
            {
                var window = _window ?? throw new InvalidOperationException("浏览器窗口尚未就绪（可能刚被关闭），请重试");
                var webView = _webView ?? throw new InvalidOperationException("浏览器窗口尚未就绪（可能刚被关闭），请重试");

                // IsIconic 只对顶层窗口有意义，子窗口的 HWND 不会带最小化状态，
                // 所以最小化与否要看宿主 Window 的状态，否则会抓到一张黑图或桌面。
                if (window.WindowState == WindowState.Minimized)
                    throw new InvalidOperationException("窗口已最小化，截不到画面；请先恢复窗口再调用");

                return WindowCapture.Capture(webView.Handle);
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    /// <summary>导航并等待 load 事件（或超时）。</summary>
    public async Task<string> NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(cancellationToken);

        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingLoad, load);

        await CdpAsync("Page.navigate", JsonParameters(new { url }), cancellationToken);

        var finished = await Task.WhenAny(load.Task, Task.Delay(LoadTimeout, cancellationToken));
        if (finished == load.Task) return url;

        // 只有确实还是自己挂的那个等待时才清理，避免误伤后续导航。
        Interlocked.CompareExchange(ref _pendingLoad, null, load);
        return $"{url}（已发出导航，但 {LoadTimeout.TotalSeconds:0} 秒内没有收到 load 事件；页面可能仍在加载，可用 console 或 evaluate 继续观察）";
    }

    /// <summary>重新加载当前页面并等待 load 事件。</summary>
    public async Task<string> ReloadAsync(bool ignoreCache, CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(cancellationToken);

        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingLoad, load);

        await CdpAsync("Page.reload", JsonParameters(new { ignoreCache }), cancellationToken);

        var finished = await Task.WhenAny(load.Task, Task.Delay(LoadTimeout, cancellationToken));
        return finished == load.Task ? "已重新加载" : "已发出重新加载，但未在超时内收到 load 事件";
    }

    /// <summary>调用任意 DevTools Protocol 方法，返回原始 JSON 响应。</summary>
    public async Task<string> CdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(cancellationToken);
        return await AwaitWithTimeout(CallCoreAsync(method, parametersJson), CallTimeout, method);
    }

    /// <summary>读宿主内累积的 console/异常记录；代理会话经管道转发到这里。</summary>
    public Task<string> ReadConsoleAsync(bool clear, CancellationToken cancellationToken = default) =>
        Task.FromResult(_console.Render(clear));

    /// <summary>读宿主内累积的网络记录；代理会话经管道转发到这里。</summary>
    public Task<string> ReadNetworkAsync(bool clear, CancellationToken cancellationToken = default) =>
        Task.FromResult(_network.Render(clear));

    /// <summary>直接投递到 UI 线程的内部调用，供启动阶段使用（那时还没法走 EnsureBrowserAsync）。</summary>
    private Task<string> CallCoreAsync(string method, string parametersJson)
    {
        var dispatcher = _dispatcher ?? throw new InvalidOperationException("UI 线程尚未启动");
        return dispatcher
            .InvokeAsync(() => _core!.CallDevToolsProtocolMethodAsync(method, parametersJson))
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

    private void SubscribeProtocolEvents()
    {
        // console.* 走 Runtime 域；浏览器自身产生的安全/CSP/网络告警走 Log 域。两者互补，都要订阅。
        Subscribe("Runtime.consoleAPICalled", _console.OnConsoleApiCalled);
        Subscribe("Runtime.exceptionThrown", _console.OnExceptionThrown);
        Subscribe("Log.entryAdded", _console.OnLogEntryAdded);

        Subscribe("Network.requestWillBeSent", _network.OnRequestWillBeSent);
        Subscribe("Network.responseReceived", _network.OnResponseReceived);
        Subscribe("Network.loadingFailed", _network.OnLoadingFailed);

        Subscribe("Page.loadEventFired", _ => OnLoadEventFired());
        Subscribe("Page.frameNavigated", OnFrameNavigated);
    }

    private void Subscribe(string eventName, Action<string> handler)
    {
        var receiver = _core!.GetDevToolsProtocolEventReceiver(eventName);
        receiver.DevToolsProtocolEventReceived += (_, args) =>
        {
            // 单个畸形事件不应该拖垮整个会话，所以这里吞掉解析异常。
            try
            {
                handler(args.ParameterObjectAsJson);
            }
            catch
            {
                // 忽略
            }
        };

        // 关键：保住引用，见字段声明处的说明。
        _receivers.Add(receiver);
    }

    private void OnLoadEventFired()
    {
        var pending = Interlocked.Exchange(ref _pendingLoad, null);
        pending?.TrySetResult();
    }

    private void OnFrameNavigated(string payloadJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("frame", out var frame)) return;
            if (!frame.TryGetProperty("url", out var url)) return;

            var value = url.GetString();
            if (string.IsNullOrEmpty(value) || _window is null) return;
            _window.Title = $"BrowserDebugMCP — {value}";
        }
        catch
        {
            // 忽略
        }
    }

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
