using System.IO;

namespace BrowserDebugMCP;

/// <summary>
/// 浏览器后端的门面：决定本进程是“宿主”还是“代理”，并在宿主消失时尝试接管。
///
/// 选举规则（同一登录会话内）：
/// - 命名互斥量已存在 → 已有宿主（或正在启动），本进程做代理，经命名管道转发调用；
/// - 互斥量不存在 → 创建并持有它，本进程成为宿主，启动 <see cref="WebViewHost"/> 与管道服务端。
///
/// 接管：代理调用时若连接层面失败（宿主进程被会话一起杀掉），且互斥量已经消失，
/// 当前进程会升级为宿主并重建浏览器，保证“任意时刻最多一个宿主、一个 WebView2”。
///
/// 串行化：多个 Agent（多个会话、子 Agent）共享同一个浏览器，页面状态无法隔离，
/// 所以所有调用——不管来自本进程的 MCP 工具还是经管道转发的代理请求——都走 <c>_callGate</c>。
/// 这保证同一时刻只有一个调用在操作浏览器，但**不提供页面状态隔离**：
/// 一个 Agent 的 open 会顶掉另一个 Agent 当前页面。
/// </summary>
internal sealed class BrowserGateway : IBrowserHost, IDisposable
{
    private readonly Func<WebViewHost> _createLocalHost;
    private readonly object _gate = new();

    // 全局串行锁：protects the shared browser (both local MCP calls and piped proxy calls).
    private readonly SemaphoreSlim _callGate = new(1, 1);

    // 排队上限：超过就明确告诉调用方“正在被别的会话占用”，而不是让它无限等或反复重试。
    private static readonly TimeSpan BusyTimeout = ResolveBusyTimeout();

    private IBrowserHost? _backend;
    private RemoteBrowserHost? _remoteHost;
    private WebViewHost? _localHost;
    private SingleInstanceHost? _pipeHost;
    private Mutex? _hostMutex;
    private volatile bool _disposed;

    // 当前持锁调用的开始时间（Environment.TickCount64 毫秒，单调时钟），0 表示空闲。
    private long _busySinceMilliseconds;

    public BrowserGateway(Func<WebViewHost> createLocalHost) => _createLocalHost = createLocalHost;

    public bool IsHost => _localHost is not null;

    /// <summary>在 MCP 服务启动前调用一次：选出宿主或代理角色。</summary>
    public void Initialize()
    {
        lock (_gate)
        {
            if (_backend is not null) return;

            if (!TryBecomeHostLocked())
            {
                _remoteHost = new RemoteBrowserHost(SingleInstanceHost.PipeName);
                _backend = _remoteHost;
            }

            Console.Error.WriteLine(IsHost
                ? "[BrowserDebugMCP] 本进程是浏览器宿主，持有唯一的 WebView2"
                : "[BrowserDebugMCP] 本进程是代理，共享已有浏览器宿主");
        }
    }

    public Task<string> NavigateAsync(string url, CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.NavigateAsync(url, cancellationToken), cancellationToken);

    public Task<string> ReloadAsync(bool ignoreCache, CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.ReloadAsync(ignoreCache, cancellationToken), cancellationToken);

    public Task<string> CdpAsync(string method, string parametersJson, CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.CdpAsync(method, parametersJson, cancellationToken), cancellationToken);

    public Task<(int Width, int Height)> ResizeBrowserAsync(
        int? width,
        int? height,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.ResizeBrowserAsync(width, height, cancellationToken), cancellationToken);

    public Task<WindowCapture.Result> CaptureWindowAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.CaptureWindowAsync(cancellationToken), cancellationToken);

    public Task<bool> CloseBrowserAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.CloseBrowserAsync(cancellationToken), cancellationToken);

    public Task<string> ReadConsoleAsync(bool clear, CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.ReadConsoleAsync(clear, cancellationToken), cancellationToken);

    public Task<string> ReadNetworkAsync(bool clear, CancellationToken cancellationToken = default) =>
        InvokeAsync(host => host.ReadNetworkAsync(clear, cancellationToken), cancellationToken);

    private async Task<T> InvokeAsync<T>(
        Func<IBrowserHost, Task<T>> call,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await _callGate.WaitAsync(BusyTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // 明显是在排队等别的 Agent：给出可操作的提示，而不是让调用方自己猜。
            throw new BrowserBusyException(DescribeBusy());
        }

        try
        {
            // 放进 try 里：之后无论哪一步抛异常，finally 都会把它清零。
            Interlocked.Exchange(ref _busySinceMilliseconds, Environment.TickCount64);

            var backend = Volatile.Read(ref _backend) ?? throw new InvalidOperationException("浏览器后端尚未初始化");

            try
            {
                return await call(backend);
            }
            catch (Exception exception) when (backend is RemoteBrowserHost && BrowserIpc.IsTransportFailure(exception))
            {
                // 宿主没了（通常是与它所属的会话一起退出）：抢到互斥量就由本进程接管。
                var local = await TryPromoteToHostAsync(cancellationToken);
                if (local is null) throw;
                return await call(local);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busySinceMilliseconds, 0);
            _callGate.Release();
        }
    }

    private static TimeSpan ResolveBusyTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("BROWSER_DEBUG_MCP_BUSY_TIMEOUT_MS");
        return int.TryParse(raw, out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : TimeSpan.FromSeconds(10);
    }

    private string DescribeBusy()
    {
        // 用单调时钟（Environment.TickCount64）而不是墙钟：NTP 校时可能让时长变成负数。
        var since = Interlocked.Read(ref _busySinceMilliseconds);
        var held = since == 0
            ? "未知"
            : $"{(Environment.TickCount64 - since) / 1000.0:0.0} 秒";

        return
            $"浏览器正被另一个会话使用（当前这次调用已占用约 {held}），本调用排队 {BusyTimeout.TotalSeconds:0} 秒仍未轮上。" +
            "多个 Agent 共享同一个 WebView2，排队是正常现象，不是故障。" +
            "请不要轮询或连续重试——重试只会再次排队；先做别的工作，隔一段时间再调用一次。";
    }

    /// <summary>
    /// 尝试接管。宿主进程刚退出时互斥量可能还没释放，所以重试几次再放弃。
    /// </summary>
    private async Task<IBrowserHost?> TryPromoteToHostAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) await Task.Delay(300, cancellationToken);

            RemoteBrowserHost? staleRemote = null;
            WebViewHost? promoted;

            lock (_gate)
            {
                if (_disposed) return null;
                if (_localHost is null && TryBecomeHostLocked())
                {
                    staleRemote = _remoteHost;
                    _remoteHost = null;
                }

                promoted = _localHost;
            }

            // 旧的代理连接不能再留。Dispose 会等正在进行的请求结束，避免与 SendAsync 抢同一把锁。
            staleRemote?.Dispose();
            if (promoted is not null) return promoted;
        }

        return null;
    }

    /// <summary>
    /// 抢宿主身份并启动本机后端。任何一步失败都要回滚：否则互斥量会一直被本进程占着，
    /// 而 _localHost 又没建起来，所有会话（包括自己）都会永久停在代理态。
    /// </summary>
    private bool TryBecomeHostLocked()
    {
        var mutex = TryCreateHostMutex();
        if (mutex is null) return false;

        WebViewHost? host = null;
        SingleInstanceHost? pipeHost = null;
        try
        {
            host = _createLocalHost();
            host.Start();

            // 先提交本地后端再开管道：否则刚连上的代理请求可能读到未初始化的 _backend。
            _localHost = host;
            _backend = host;

            // 管道请求交给 gateway 而不是 host：这样代理请求与宿主自己的 MCP 调用
            // 共用 _callGate，多个 Agent 之间才真正串行。
            pipeHost = new SingleInstanceHost(this);
            pipeHost.Start();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[BrowserDebugMCP] 启动本机宿主失败，退回代理模式：{exception}");
            _localHost = null;
            _backend = null;
            pipeHost?.Dispose();
            host?.Dispose();
            mutex.Dispose();
            return false;
        }

        _hostMutex = mutex;
        _pipeHost = pipeHost;
        Console.Error.WriteLine("[BrowserDebugMCP] 已接管浏览器宿主，重建唯一的 WebView2");
        return true;
    }

    /// <summary>创建并持有宿主互斥量；已存在（说明有宿主）时返回 null。</summary>
    private static Mutex? TryCreateHostMutex()
    {
        try
        {
            using var existing = Mutex.OpenExisting(SingleInstanceHost.MutexName);
            return null;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // 没有宿主，继续尝试创建。
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var mutex = new Mutex(initiallyOwned: true, SingleInstanceHost.MutexName, out var createdNew);
        if (createdNew) return mutex;

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // 先停管道服务端，再销毁浏览器，最后放掉互斥量。
            _pipeHost?.Dispose();
            _localHost?.Dispose();
            _remoteHost?.Dispose();
            _hostMutex?.Dispose();
            _remoteHost = null;
            _localHost = null;
            _pipeHost = null;
            _backend = null;
            // 不 Dispose _callGate：还在等待/执行的调用（含退出中的 handler）可能仍要 Release。
        }
    }
}
