using System.Text;
using System.Text.Json;

namespace BrowserDebugMCP;

/// <summary>
/// 累积 Network 域推送的请求/响应/失败事件，用来回答"这个页面到底请求了什么、哪一条失败了"。
/// 事件来自 WebView2 的原生回调线程，所以内部用锁保护。
/// </summary>
internal sealed class NetworkBuffer
{
    private const int MaxEntries = 1000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Request> _requests = new();
    private readonly Queue<string> _order = new();

    private sealed class Request
    {
        public string Url = string.Empty;
        public string Method = string.Empty;
        public string ResourceType = string.Empty;
        public int? Status;
        public string? Failure;
        public string? MimeType;
    }

    public void OnRequestWillBeSent(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("requestId", out var idElement)) return;
        var id = idElement.GetString();
        if (string.IsNullOrEmpty(id)) return;

        var request = new Request();
        if (root.TryGetProperty("request", out var payload))
        {
            request.Url = payload.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty;
            request.Method = payload.TryGetProperty("method", out var method) ? method.GetString() ?? string.Empty : string.Empty;
        }
        if (root.TryGetProperty("type", out var type)) request.ResourceType = type.GetString() ?? string.Empty;

        lock (_gate)
        {
            _requests[id] = request;
            _order.Enqueue(id);
            Trim();
        }
    }

    public void OnResponseReceived(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("requestId", out var idElement)) return;
        var id = idElement.GetString();
        if (string.IsNullOrEmpty(id)) return;

        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var request)) return;
            if (!root.TryGetProperty("response", out var response)) return;

            if (response.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number)
                request.Status = status.GetInt32();
            if (response.TryGetProperty("mimeType", out var mime) && mime.ValueKind == JsonValueKind.String)
                request.MimeType = mime.GetString();
            if (string.IsNullOrEmpty(request.Url) && response.TryGetProperty("url", out var url))
                request.Url = url.GetString() ?? string.Empty;
        }
    }

    public void OnLoadingFailed(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        if (!root.TryGetProperty("requestId", out var idElement)) return;
        var id = idElement.GetString();
        if (string.IsNullOrEmpty(id)) return;

        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var request)) return;
            request.Failure = root.TryGetProperty("errorText", out var error) ? error.GetString() : "未知错误";
        }
    }

    /// <summary>渲染成文本；<paramref name="clear"/> 为 true 时同时清空。</summary>
    public string Render(bool clear)
    {
        lock (_gate)
        {
            if (_order.Count == 0) return "（暂无网络请求记录）";

            var builder = new StringBuilder();
            foreach (var id in _order)
            {
                if (!_requests.TryGetValue(id, out var request)) continue;

                if (request.Failure is not null)
                    builder.Append("失败  ").Append(request.Method).Append(' ').Append(request.Url)
                        .Append("  ← ").Append(request.Failure);
                else
                    builder.Append(request.Status?.ToString() ?? "…").Append("  ")
                        .Append(request.Method).Append(' ').Append(request.Url);

                if (!string.IsNullOrEmpty(request.ResourceType)) builder.Append("  (").Append(request.ResourceType).Append(')');
                if (!string.IsNullOrEmpty(request.MimeType)) builder.Append(' ').Append(request.MimeType);
                builder.Append('\n');
            }

            if (clear)
            {
                _requests.Clear();
                _order.Clear();
            }

            return builder.ToString();
        }
    }

    private void Trim()
    {
        while (_order.Count > MaxEntries)
        {
            var oldest = _order.Dequeue();
            _requests.Remove(oldest);
        }
    }
}
