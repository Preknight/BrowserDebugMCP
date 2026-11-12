using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BrowserDebugMCP;

/// <summary>
/// 代理进程里的浏览器后端：所有调用经命名管道转给宿主。
///
/// 连接失败会重置连接并抛出 IO 异常，<see cref="BrowserGateway"/> 据此判断宿主是否已经
/// 退出、要不要由当前进程接管。
/// </summary>
internal sealed class RemoteBrowserHost(string pipeName) : IBrowserHost, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    // 一条连接上同一时刻只发一个请求，响应才不会错位。
    private readonly SemaphoreSlim _io = new(1, 1);

    private NamedPipeClientStream? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId;
    private int _disposed;

    public async Task<string> NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        ValueAsString(await SendAsync("navigate", [url], cancellationToken));

    public async Task<string> ReloadAsync(bool ignoreCache, CancellationToken cancellationToken = default) =>
        ValueAsString(await SendAsync("reload", [ignoreCache], cancellationToken));

    public async Task<string> CdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default) =>
        ValueAsString(await SendAsync("cdp", [method, parametersJson], cancellationToken));

    public async Task<(int Width, int Height)> ResizeBrowserAsync(
        int? width,
        int? height,
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("resize", [width, height], cancellationToken);
        return (result.GetProperty("width").GetInt32(), result.GetProperty("height").GetInt32());
    }

    public async Task<WindowCapture.Result> CaptureWindowAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("captureWindow", [], cancellationToken);
        return new WindowCapture.Result(
            Convert.FromBase64String(result.GetProperty("png").GetString() ?? string.Empty),
            result.GetProperty("width").GetInt32(),
            result.GetProperty("height").GetInt32(),
            result.GetProperty("method").GetString() ?? "未知");
    }

    public async Task<bool> CloseBrowserAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("closeWindow", [], cancellationToken);
        return result.ValueKind == JsonValueKind.True;
    }

    public async Task<string> ReadConsoleAsync(bool clear, CancellationToken cancellationToken = default) =>
        ValueAsString(await SendAsync("console", [clear], cancellationToken));

    public async Task<string> ReadNetworkAsync(bool clear, CancellationToken cancellationToken = default) =>
        ValueAsString(await SendAsync("network", [clear], cancellationToken));

    private async Task<JsonElement> SendAsync(string method, object?[] args, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _io.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);

            // 取局部引用：Dispose/ResetConnection 可能同时把字段置空，用字段读会变成 NRE，
            // 而 NRE 不算传输失败，会把异常直接抛给工具调用者。
            var writer = _writer ?? throw new IOException("浏览器宿主连接尚未建立");
            var reader = _reader ?? throw new IOException("浏览器宿主连接尚未建立");

            var id = Interlocked.Increment(ref _nextId);
            await writer.WriteLineAsync(BrowserIpc.CreateRequest(id, method, args).AsMemory(), cancellationToken);

            var line = await reader
                .ReadLineAsync(cancellationToken)
                .AsTask()
                .WaitAsync(RequestTimeout, cancellationToken)
                ?? throw new IOException("浏览器宿主已断开连接（未收到响应）");

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement)
                && idElement.TryGetInt64(out var responseId)
                && responseId != id)
            {
                throw new IOException($"浏览器宿主响应错位（期望 {id}，收到 {responseId}）");
            }

            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var message = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
                throw new BrowserHostException(message ?? "浏览器宿主返回了未知错误");
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
        catch (OperationCanceledException)
        {
            // 取消可能发生在读响应的中途，连接状态不可再用，丢掉重连。
            ResetConnection();
            throw;
        }
        catch (Exception exception) when (BrowserIpc.IsTransportFailure(exception))
        {
            ResetConnection();
            throw;
        }
        finally
        {
            // Dispose 可能已经释放了 _io（改走本地后端时），这里不能再抛。
            try { _io.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true }) return;
        ResetConnection();

        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await client.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);
                _client = client;
                _reader = new StreamReader(client, Encoding.UTF8, false, 16 * 1024, leaveOpen: true);
                _writer = new StreamWriter(client, new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
                {
                    AutoFlush = true,
                };
                return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastError = exception;
                client.Dispose();
                if (attempt < 2) await Task.Delay(250, cancellationToken);
            }
        }

        throw new IOException("无法连接浏览器宿主（可能已退出）", lastError);
    }

    private void ResetConnection()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
    }

    public void Dispose()
    {
        // 幂等：并发 Dispose（网关切换后端与进程退出可能同时发生）只允许一次。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 先等正在进行的请求结束，否则 _io.Dispose 与 SendAsync 的 finally Release 并发
        // 属于未定义行为（Release 会抛 ObjectDisposedException，覆盖真实结果）。
        var acquired = false;
        try
        {
            acquired = _io.Wait(TimeSpan.FromSeconds(2));
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // 即使没等到（有请求卡在 I/O），也关掉连接让它尽快失败退出。
            ResetConnection();
        }
        finally
        {
            if (acquired) _io.Release();
            _io.Dispose();
        }
    }

    private static string ValueAsString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
}
