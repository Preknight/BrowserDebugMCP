using System.IO;
using System.Text.Json;

namespace BrowserDebugMCP;

/// <summary>
/// 宿主与代理之间的 IPC 协议：一行一个 JSON 请求，一行一个 JSON 响应。
///
/// 请求：<c>{"id":1,"method":"navigate","args":["http://localhost/"]}</c>
/// 响应：<c>{"id":1,"ok":true,"result":...}</c> 或 <c>{"id":1,"ok":false,"error":"..."}</c>
///
/// 只做方法分发，不关心传输：宿主侧由 <see cref="SingleInstanceHost"/> 调用，
/// 代理侧由 <see cref="RemoteBrowserHost"/> 组装请求。
/// </summary>
internal static class BrowserIpc
{
    public static string CreateRequest(long id, string method, object?[] args) =>
        JsonSerializer.Serialize(new { id, method, args });

    /// <summary>执行一条请求并返回响应行；业务异常与协议异常都会转成 ok=false。</summary>
    public static async Task<string> DispatchAsync(
        IBrowserHost host,
        string requestLine,
        CancellationToken cancellationToken)
    {
        long id = 0;
        try
        {
            using var document = JsonDocument.Parse(requestLine);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId)) id = parsedId;

            var method = root.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString() ?? string.Empty
                : string.Empty;

            var hasArgs = root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array;
            var result = await ExecuteAsync(host, method, hasArgs ? args : default, cancellationToken);

            return JsonSerializer.Serialize(new { id, ok = true, result });
        }
        catch (Exception exception)
        {
            return JsonSerializer.Serialize(new
            {
                id,
                ok = false,
                error = exception.Message,
                errorType = exception.GetType().Name,
            });
        }
    }

    /// <summary>连接层面的失败：代理据此判断宿主是否已经退出、要不要尝试接管。</summary>
    /// <remarks>
    /// 只认 <see cref="IOException"/>（含 <see cref="EndOfStreamException"/>）。
    /// 不包含 <see cref="ObjectDisposedException"/>：它通常意味着本进程已经切走/释放了旧的
    /// 远程后端。也不包含 <see cref="TimeoutException"/>：那是单个请求等太久，宿主可能还活着，
    /// 重放调用会产生重复副作用（例如重复导航），所以直接抛给调用方。
    /// </remarks>
    public static bool IsTransportFailure(Exception exception) => exception is IOException;

    private static async Task<object?> ExecuteAsync(
        IBrowserHost host,
        string method,
        JsonElement args,
        CancellationToken cancellationToken) =>
        method switch
        {
            "navigate" => await host.NavigateAsync(RequiredString(args, 0), cancellationToken),
            "reload" => await host.ReloadAsync(OptionalBool(args, 0), cancellationToken),
            "cdp" => await host.CdpAsync(RequiredString(args, 0), RequiredString(args, 1), cancellationToken),
            "resize" => SizeResult(await host.ResizeBrowserAsync(
                OptionalInt(args, 0), OptionalInt(args, 1), cancellationToken)),
            "captureWindow" => CaptureResult(await host.CaptureWindowAsync(cancellationToken)),
            "closeWindow" => await host.CloseBrowserAsync(cancellationToken),
            "console" => await host.ReadConsoleAsync(OptionalBool(args, 0), cancellationToken),
            "network" => await host.ReadNetworkAsync(OptionalBool(args, 0), cancellationToken),
            _ => throw new InvalidOperationException($"未知的 IPC 方法：{method}"),
        };

    private static object SizeResult((int Width, int Height) size) =>
        new { width = size.Width, height = size.Height };

    private static object CaptureResult(WindowCapture.Result capture) => new
    {
        png = Convert.ToBase64String(capture.Png),
        width = capture.Width,
        height = capture.Height,
        method = capture.Method,
    };

    private static JsonElement At(JsonElement args, int index) =>
        args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > index
            ? args[index]
            : throw new InvalidOperationException($"IPC 参数 {index} 缺失");

    private static string RequiredString(JsonElement args, int index) =>
        At(args, index).ValueKind == JsonValueKind.String
            ? At(args, index).GetString()!
            : throw new InvalidOperationException($"IPC 参数 {index} 必须是字符串");

    private static bool OptionalBool(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index) return false;
        return args[index].ValueKind == JsonValueKind.True;
    }

    private static int? OptionalInt(JsonElement args, int index)
    {
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() <= index) return null;
        var element = args[index];
        if (element.ValueKind == JsonValueKind.Null) return null;
        return element.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"IPC 参数 {index} 必须是整数");
    }
}
