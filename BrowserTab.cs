using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BrowserDebugMCP;

/// <summary>
/// 一个标签页 = 一块独立的 WebView2 实例，外加它自己的 console/network 缓冲。
///
/// 为什么缓冲要按标签分开：多个标签可能同时存活，如果把事件都塞进同一个缓冲，
/// 切换标签后读到的 console/network 会混着别的页面，排查时无法归因。
/// 活动标签是哪一块，就交给它自己的缓冲——MCP 的 console/network 工具读到的
/// 永远是"当前展示的那个页面"。
///
/// 注意：标签切换只改 Visibility，不把控件移出视觉树。WebView2 的 WPF 宿主是
/// <c>HwndHost</c>，一旦离开视觉树，它的原生窗口会被销毁、CoreWebView2 随之失效，
/// 重新加入等于重建浏览器（页面与 JS 状态全丢）。用 Collapsed 隐藏能同时满足
/// "一次只展示一个"和"各标签状态互不干扰"。
/// </summary>
internal sealed class BrowserTab : IDisposable
{
    /// <summary>
    /// 必须长期持有 receiver 的引用：内联订阅会被 GC 回收，
    /// 之后原生层触发回调时会造成随机内存访问违例（见 README 的踩坑清单）。
    /// </summary>
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _receivers = new();

    /// <summary>由 Page.loadEventFired 推动；导航前先挂上等待，避免事件先于等待到达。</summary>
    private TaskCompletionSource? _pendingLoad;

    public BrowserTab(WebView2 webView) => WebView = webView;

    public WebView2 WebView { get; }

    public CoreWebView2 Core { get; private set; } = null!;

    public ConsoleBuffer Console { get; } = new();

    public NetworkBuffer Network { get; } = new();

    public string Title { get; private set; } = "新标签页";

    public string Url { get; private set; } = "about:blank";

    public bool CanGoBack => Core?.CanGoBack ?? false;

    public bool CanGoForward => Core?.CanGoForward ?? false;

    /// <summary>标题 / 地址 / 前进后退状态发生变化，窗口据此刷新标签条与工具栏。</summary>
    public event Action<BrowserTab>? StateChanged;

    /// <summary>页面要求另开窗口（target=_blank、window.open），宿主会为它新建标签。</summary>
    public event Action<BrowserTab, string>? NewWindowRequested;

    /// <summary>绑定核心对象并订阅事件。每个标签只在创建时调用一次。</summary>
    public void Attach(CoreWebView2 core)
    {
        Core = core;
        _receivers.Clear();

        // console.* 走 Runtime 域；浏览器自身的安全/CSP/网络告警走 Log 域。两者互补。
        Subscribe("Runtime.consoleAPICalled", Console.OnConsoleApiCalled);
        Subscribe("Runtime.exceptionThrown", Console.OnExceptionThrown);
        Subscribe("Log.entryAdded", Console.OnLogEntryAdded);

        Subscribe("Network.requestWillBeSent", Network.OnRequestWillBeSent);
        Subscribe("Network.responseReceived", Network.OnResponseReceived);
        Subscribe("Network.loadingFailed", Network.OnLoadingFailed);

        Subscribe("Page.loadEventFired", _ => OnLoadEventFired());
        Subscribe("Page.frameNavigated", OnFrameNavigated);

        core.SourceChanged += (_, _) =>
        {
            Url = string.IsNullOrWhiteSpace(core.Source) ? Url : core.Source;
            RaiseStateChanged();
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "新标签页" : core.DocumentTitle;
            RaiseStateChanged();
        };

        // 前进后退是否可用只有在历史变化后才准，所以按钮状态跟着它刷新。
        core.HistoryChanged += (_, _) => RaiseStateChanged();

        core.NewWindowRequested += (_, e) =>
        {
            // 不接管的话 WebView2 会直接忽略 target=_blank，用户点链接像是坏了。
            e.Handled = true;
            NewWindowRequested?.Invoke(this, e.Uri);
        };
    }

    /// <summary>导航/刷新前调用：挂一个新的 load 等待，并让上一轮等待立刻结束。</summary>
    public TaskCompletionSource BeginLoadWait()
    {
        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _pendingLoad, wait)?.TrySetResult();
        return wait;
    }

    /// <summary>超时后清理自己挂的等待，避免误伤后续导航。</summary>
    public void AbandonLoadWait(TaskCompletionSource wait) =>
        Interlocked.CompareExchange(ref _pendingLoad, null, wait);

    public void Dispose()
    {
        _receivers.Clear();
        Interlocked.Exchange(ref _pendingLoad, null)?.TrySetResult();

        try
        {
            // 必须在 UI 线程调用；调用方（WebViewHost）保证这一点。
            WebView.Dispose();
        }
        catch
        {
            // 关闭阶段的异常无需上报
        }
    }

    private void OnLoadEventFired() =>
        Interlocked.Exchange(ref _pendingLoad, null)?.TrySetResult();

    private void OnFrameNavigated(string payloadJson)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty("frame", out var frame)) return;
            if (!frame.TryGetProperty("url", out var url)) return;

            var value = url.GetString();
            if (string.IsNullOrEmpty(value)) return;

            Url = value;
            RaiseStateChanged();
        }
        catch
        {
            // 单个畸形事件不应该拖垮整个会话
        }
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this);
        }
        catch
        {
            // UI 刷新失败不该影响页面本身
        }
    }

    private void Subscribe(string eventName, Action<string> handler)
    {
        var receiver = Core.GetDevToolsProtocolEventReceiver(eventName);
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
}
