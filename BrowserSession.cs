namespace BrowserDebugMCP;

/// <summary>
/// 把一次调试会话里共享的东西聚在一起，方便 MCP 的工具类用构造函数注入。
/// </summary>
internal sealed class BrowserSession(
    WebViewHost host,
    ConsoleBuffer console,
    NetworkBuffer network,
    string screenshotDir)
{
    public WebViewHost Host { get; } = host;

    public ConsoleBuffer Console { get; } = console;

    public NetworkBuffer Network { get; } = network;

    public string ScreenshotDir { get; } = screenshotDir;
}
