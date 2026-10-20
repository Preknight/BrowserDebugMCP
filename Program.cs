using System.IO;
using BrowserDebugMCP;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// 一个把 WebView2 包装成 MCP 工具的 stdio 服务器：
// Reasonix 通过管道调用它，它再用 WebView2 内建的 DevTools Protocol 客户端驱动页面。
//
// 关键约束：MCP 的 stdio 传输用 stdout 传 JSON-RPC，所以任何日志都不能写 stdout。

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    // 全部日志导到 stderr，否则会污染 stdout 上的协议流，直接把连接打断。
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

// 浏览器数据目录放在 exe 同级，不写 %APPDATA%；可用环境变量覆盖，
// 这样调试会话之间既隔离，又不会碰到你日常 Edge 的 profile。
var userDataFolder = ResolveUserDataFolder();
var screenshotDir = ResolveScreenshotDir();
// 不在这里预创建浏览器数据目录：浏览器是首次工具调用才建的，提前建只会留个空壳。
Directory.CreateDirectory(screenshotDir);

var consoleBuffer = new ConsoleBuffer();
var networkBuffer = new NetworkBuffer();
var webViewHost = new WebViewHost(userDataFolder, consoleBuffer, networkBuffer);
var session = new BrowserSession(webViewHost, consoleBuffer, networkBuffer, screenshotDir);

builder.Services.AddSingleton(session);
builder.Services.AddSingleton(consoleBuffer);
builder.Services.AddSingleton(networkBuffer);
builder.Services.AddSingleton(webViewHost);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BrowserDebugMCP");

logger.LogInformation("浏览器数据目录：{UserDataFolder}", userDataFolder);
logger.LogInformation("截图输出目录：{ScreenshotDir}", screenshotDir);

// 任何漏网的异常都要留下痕迹，否则只能看到 MCP 客户端给出的一句笼统错误。
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[BrowserDebugMCP] 未处理异常：{e.ExceptionObject}");

// 只启动常驻的 UI 消息循环，此刻不创建浏览器：窗口要等第一次工具调用才出现。
// 这样 Reasonix 拉起服务器时不会弹窗；用户关掉窗口后进程也照旧活着，
// 不会被客户端判定为断开而自动重连（那正是"窗口关了又自己冒出来"的原因）。
webViewHost.Start();

try
{
    await app.RunAsync();
}
finally
{
    webViewHost.Dispose();
}

return 0;

static string ResolveUserDataFolder()
{
    var configured = Environment.GetEnvironmentVariable("BROWSER_DEBUG_MCP_USER_DATA");
    return string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(AppContext.BaseDirectory, "webview2-data")
        : Path.GetFullPath(configured);
}

static string ResolveScreenshotDir()
{
    var configured = Environment.GetEnvironmentVariable("BROWSER_DEBUG_MCP_SCREENSHOT_DIR");
    return string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(AppContext.BaseDirectory, "screenshots")
        : Path.GetFullPath(configured);
}
