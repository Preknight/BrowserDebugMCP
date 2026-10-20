using System.Text;
using System.Text.Json;

namespace BrowserDebugMCP;

/// <summary>
/// 累积 Runtime / Log 域推送过来的 console 输出、未捕获异常与浏览器自身告警。
/// 事件来自 WebView2 的原生回调线程，所以内部用锁保护。
/// </summary>
internal sealed class ConsoleBuffer
{
    private const int MaxEntries = 2000;

    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();

    private readonly record struct Entry(string Kind, string Text, string Source);

    /// <summary>Runtime.consoleAPICalled —— 页面里 console.log / warn / error 等。</summary>
    public void OnConsoleApiCalled(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        var kind = root.TryGetProperty("type", out var type) ? type.GetString() ?? "log" : "log";
        var text = string.Join(" ", ReadConsoleArguments(root));
        Add(new Entry(kind, text, ReadTopFrame(root)));
    }

    /// <summary>Runtime.exceptionThrown —— 未捕获的 JS 异常。</summary>
    public void OnExceptionThrown(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("exceptionDetails", out var details)) return;

        var text = "未捕获异常";
        if (details.TryGetProperty("exception", out var exception)
            && exception.TryGetProperty("description", out var description)
            && description.ValueKind == JsonValueKind.String)
        {
            text = description.GetString()!;
        }
        else if (details.TryGetProperty("text", out var plain) && plain.ValueKind == JsonValueKind.String)
        {
            text = plain.GetString()!;
        }

        var url = details.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        var line = details.TryGetProperty("lineNumber", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32() + 1
            : 0;

        Add(new Entry("exception", text, string.IsNullOrEmpty(url) ? line.ToString() : $"{url}:{line}"));
    }

    /// <summary>Log.entryAdded —— 浏览器产生的告警（CSP、混合内容、加载失败等），不含 console.* 输出。</summary>
    public void OnLogEntryAdded(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (!document.RootElement.TryGetProperty("entry", out var entry)) return;

        var level = entry.TryGetProperty("level", out var l) ? l.GetString() ?? "info" : "info";
        var text = entry.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var url = entry.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;

        Add(new Entry($"browser/{level}", text, url));
    }

    /// <summary>把缓冲渲染成文本；<paramref name="clear"/> 为 true 时同时清空。</summary>
    public string Render(bool clear)
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return "（暂无 console 输出）";

            var builder = new StringBuilder();
            foreach (var entry in _entries)
            {
                builder.Append('[').Append(entry.Kind).Append("] ").Append(entry.Text);
                if (!string.IsNullOrEmpty(entry.Source)) builder.Append("  @ ").Append(entry.Source);
                builder.Append('\n');
            }

            if (clear) _entries.Clear();
            return builder.ToString();
        }
    }

    private void Add(Entry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
    }

    private static IEnumerable<string> ReadConsoleArguments(JsonElement root)
    {
        if (!root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) yield break;

        foreach (var argument in args.EnumerateArray())
        {
            if (argument.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Undefined)
            {
                yield return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            }
            else if (argument.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
            {
                // 对象/函数没有 value，退回它的 description（例如 "Object"、"ReferenceError: ..."）。
                yield return description.GetString()!;
            }
            else if (argument.TryGetProperty("type", out var type))
            {
                yield return $"<{type.GetString()}>";
            }
        }
    }

    private static string ReadTopFrame(JsonElement root)
    {
        if (!root.TryGetProperty("stackTrace", out var stackTrace)) return string.Empty;
        if (!stackTrace.TryGetProperty("callFrames", out var frames)) return string.Empty;
        if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() == 0) return string.Empty;

        var frame = frames[0];
        var url = frame.TryGetProperty("url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        var line = frame.TryGetProperty("lineNumber", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32() + 1
            : 0;

        if (string.IsNullOrEmpty(url)) return line > 0 ? line.ToString() : string.Empty;
        return $"{url}:{line}";
    }
}
