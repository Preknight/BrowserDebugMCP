namespace BrowserDebugMCP;

/// <summary>
/// 把一次调试会话里共享的东西聚在一起，方便 MCP 的工具类用构造函数注入。
///
/// <see cref="Host"/> 可能是本机宿主（<see cref="WebViewHost"/>），也可能是经命名管道
/// 转发到宿主的代理（<see cref="RemoteBrowserHost"/>）；工具层不需要区分。
/// </summary>
internal sealed class BrowserSession(IBrowserHost host, string screenshotDir)
{
    public IBrowserHost Host { get; } = host;

    public string ScreenshotDir { get; } = screenshotDir;
}
