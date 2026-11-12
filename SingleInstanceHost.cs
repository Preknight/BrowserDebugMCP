using System.IO;
using System.IO.Pipes;
using System.Text;

namespace BrowserDebugMCP;

/// <summary>
/// 单实例宿主：持有唯一的浏览器后端，并在命名管道上为其他会话的代理进程服务。
///
/// 谁是宿主由 <see cref="BrowserGateway"/> 用命名互斥量选出；本类只负责管道服务端。
/// 请求的串行化统一由 <see cref="BrowserGateway"/> 的全局锁负责（这样宿主自己的 MCP
/// 调用与代理请求才共用同一把锁），本类只负责“一连接一处理”。
/// </summary>
internal sealed class SingleInstanceHost : IDisposable
{
    /// <summary>同一登录会话内唯一的宿主互斥量名。</summary>
    public const string MutexName = @"Local\BrowserDebugMCP.Host";

    /// <summary>管道名带用户名：命名管道是全局命名空间，不同用户不该抢同一个名字。</summary>
    public static readonly string PipeName = $"BrowserDebugMCP.Host.{Environment.UserName}";

    private readonly IBrowserHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _handlers = new();
    private readonly object _handlersGate = new();
    private Task? _acceptLoop;
    private volatile bool _stopping;

    public SingleInstanceHost(IBrowserHost host) => _host = host;

    public void Start() => _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_stopping)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (Exception exception)
                {
                    Log("创建管道", exception);
                    await Task.Delay(500, CancellationToken.None);
                    continue;
                }

                try
                {
                    await server.WaitForConnectionAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    server.Dispose();
                    return;
                }
                catch (Exception exception)
                {
                    Log("等待代理连接", exception);
                    server.Dispose();
                    continue;
                }

                if (_stopping)
                {
                    server.Dispose();
                    return;
                }

                // 每个代理一个连接，互不阻塞；具体请求在 HandleClientAsync 里处理。
                var handler = HandleClientAsync(server, cancellationToken);
                lock (_handlersGate) _handlers.Add(handler);
                _ = handler.ContinueWith(
                    completed =>
                    {
                        lock (_handlersGate) _handlers.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception exception)
        {
            // 接受循环本身不应该静默死掉：记日志，宿主仍能处理已建立的连接。
            Log("接受循环", exception);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using (server)
        using (var reader = new StreamReader(server, Encoding.UTF8, false, 16 * 1024, leaveOpen: true))
        using (var writer = new StreamWriter(server, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null) return;

                    // 串行化在 BrowserGateway 里统一做，这里只管转发。
                    var response = await BrowserIpc.DispatchAsync(_host, line, cancellationToken);
                    await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // 进程正在退出
            }
            catch (IOException)
            {
                // 代理断开
            }
            catch (Exception exception)
            {
                Log("处理代理请求", exception);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try { _cts.Cancel(); } catch { /* 已释放 */ }

        // 整体限时 3 秒：宿主退出是在持有 BrowserGateway._gate 的情况下同步调用的，
        // 不能因为某个 handler 卡住就把 MCP 退出拖成好几秒。
        var deadline = DateTime.UtcNow.AddSeconds(3);

        try { _acceptLoop?.Wait(Remaining(deadline)); } catch { /* 退出阶段忽略 */ }

        // 循环排空：挂断时可能还有 handler 刚被加入快照之外。
        for (var round = 0; round < 3; round++)
        {
            Task[] handlers;
            lock (_handlersGate) handlers = _handlers.ToArray();
            if (handlers.Length == 0) break;

            var remaining = Remaining(deadline);
            if (remaining <= TimeSpan.Zero) break;

            try { Task.WaitAll(handlers, remaining); } catch { /* 退出阶段忽略 */ }
        }

        // 故意不 Dispose _cts：仍在退出的 handler 可能还会用它的 Token 注册回调。
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void Log(string stage, Exception exception) =>
        Console.Error.WriteLine($"[BrowserDebugMCP] 宿主 {stage} 失败：{exception}");
}
