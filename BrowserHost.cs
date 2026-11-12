namespace BrowserDebugMCP;

/// <summary>
/// 浏览器后端的统一入口。
///
/// 只有“宿主”进程真正持有 WebView2；其他会话的进程是代理，把这组调用经命名管道转发给宿主。
/// 工具层只依赖这个接口，因此宿主与代理的行为对 MCP 调用方完全一致。
/// </summary>
internal interface IBrowserHost
{
    Task<string> NavigateAsync(string url, CancellationToken cancellationToken = default);

    Task<string> ReloadAsync(bool ignoreCache, CancellationToken cancellationToken = default);

    Task<string> CdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default);

    Task<(int Width, int Height)> ResizeBrowserAsync(int? width, int? height, CancellationToken cancellationToken = default);

    Task<WindowCapture.Result> CaptureWindowAsync(CancellationToken cancellationToken = default);

    Task<bool> CloseBrowserAsync(CancellationToken cancellationToken = default);

    Task<string> ReadConsoleAsync(bool clear, CancellationToken cancellationToken = default);

    Task<string> ReadNetworkAsync(bool clear, CancellationToken cancellationToken = default);
}

/// <summary>宿主明确返回的业务错误（不是连接层面的失败）。</summary>
internal sealed class BrowserHostException(string message) : Exception(message);

/// <summary>
/// 浏览器正被另一个会话占用，排队等到超时。
///
/// 消息里要明确写“不要轮询”：多个 Agent 共享同一个 WebView2 时，忙是正常状态，
/// 连续重试只会继续排队并各自等满超时。
/// </summary>
internal sealed class BrowserBusyException(string message) : Exception(message);
