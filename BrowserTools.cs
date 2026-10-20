using System.ComponentModel;
using System.IO;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace BrowserDebugMCP;

/// <summary>
/// 暴露给 Reasonix 的工具集。除了常规操作，还提供 <c>cdp</c> 这个逃生舱，
/// 因此即使某个能力没有被单独封装，也能用原始 DevTools Protocol 方法补上。
/// </summary>
[McpServerToolType]
internal sealed class BrowserTools(BrowserSession session)
{
    private const int MaxHtmlLength = 60_000;

    [McpServerTool(Name = "open")]
    [Description("在受控浏览器里打开一个 URL，并等待页面 load 事件（最多 20 秒）。")]
    public Task<string> Open(
        [Description("完整地址，例如 http://localhost:5000/")] string url,
        CancellationToken cancellationToken) =>
        session.Host.NavigateAsync(url, cancellationToken);

    [McpServerTool(Name = "reload")]
    [Description("重新加载当前页面并等待 load 事件。ignoreCache 为 true 时强制绕过缓存。")]
    public Task<string> Reload(
        [Description("是否绕过缓存，默认 false")] bool ignoreCache = false,
        CancellationToken cancellationToken = default) =>
        session.Host.ReloadAsync(ignoreCache, cancellationToken);

    [McpServerTool(Name = "evaluate")]
    [Description("在当前页面执行 JavaScript 表达式并返回结果。会自动等待 Promise，可用来读取任意页面状态。")]
    public async Task<string> Evaluate(
        [Description("要执行的 JS 表达式，例如 document.title 或 (() => { return [...document.querySelectorAll('div')].length; })()")] string expression,
        CancellationToken cancellationToken)
    {
        var parameters = JsonSerializer.Serialize(new
        {
            expression,
            returnByValue = true,
            awaitPromise = true,
            userGesture = true,
        });

        var raw = await session.Host.CdpAsync("Runtime.evaluate", parameters, cancellationToken);
        return SummarizeEvaluation(raw);
    }

    [McpServerTool(Name = "console")]
    [Description("读取页面累积的 console 输出、未捕获异常与浏览器自身告警（CSP、混合内容、资源加载失败等）。")]
    public string ReadConsole(
        [Description("读取后是否清空缓冲，默认 false")] bool clear = false) =>
        session.Console.Render(clear);

    [McpServerTool(Name = "network")]
    [Description("读取页面累积的网络请求记录：状态码、方法、URL、资源类型，以及失败原因。")]
    public string ReadNetwork(
        [Description("读取后是否清空记录，默认 false")] bool clear = false) =>
        session.Network.Render(clear);

    [McpServerTool(Name = "dom")]
    [Description("取当前文档（或某个元素）的 outerHTML。用于确认前端渲染出来的真实结构。")]
    public async Task<string> Dom(
        [Description("可选的 CSS 选择器；省略则返回整个文档")] string? selector = null,
        CancellationToken cancellationToken = default)
    {
        var documentRaw = await session.Host.CdpAsync("DOM.getDocument", "{\"depth\":1}", cancellationToken);
        int rootNodeId;
        using (var document = JsonDocument.Parse(documentRaw))
        {
            rootNodeId = document.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
        }

        var nodeId = rootNodeId;
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var queryParameters = JsonSerializer.Serialize(new { nodeId = rootNodeId, selector });
            var queryRaw = await session.Host.CdpAsync("DOM.querySelector", queryParameters, cancellationToken);

            using var query = JsonDocument.Parse(queryRaw);
            nodeId = query.RootElement.GetProperty("nodeId").GetInt32();
            if (nodeId == 0) return $"没有元素匹配选择器：{selector}";
        }

        var htmlParameters = JsonSerializer.Serialize(new { nodeId });
        var htmlRaw = await session.Host.CdpAsync("DOM.getOuterHTML", htmlParameters, cancellationToken);

        using var htmlDocument = JsonDocument.Parse(htmlRaw);
        var html = htmlDocument.RootElement.GetProperty("outerHTML").GetString() ?? string.Empty;

        return html.Length <= MaxHtmlLength
            ? html
            : html[..MaxHtmlLength] + $"\n…（已截断，原始长度 {html.Length} 字符）";
    }

    [McpServerTool(Name = "screenshot")]
    [Description("截取当前页面并保存为 PNG，返回文件路径。")]
    public async Task<string> Screenshot(
        [Description("可选文件名；省略则按时间戳生成。只会取文件名，不接受路径")] string? fileName = null,
        CancellationToken cancellationToken = default)
    {
        var raw = await session.Host.CdpAsync("Page.captureScreenshot", "{\"format\":\"png\"}", cancellationToken);

        string base64;
        using (var document = JsonDocument.Parse(raw))
        {
            if (!document.RootElement.TryGetProperty("data", out var data)) return "截图失败：" + raw;
            base64 = data.GetString() ?? string.Empty;
        }

        var bytes = Convert.FromBase64String(base64);
        var path = Path.Combine(session.ScreenshotDir, SanitizeFileName(fileName));
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        return path;
    }

    [McpServerTool(Name = "cdp")]
    [Description("直接调用任意 DevTools Protocol 方法（逃生舱），返回原始 JSON。例如 method=Accessibility.getFullAXTree、method=Performance.getMetrics。")]
    public Task<string> Cdp(
        [Description("形如 Domain.method 的方法名")] string method,
        [Description("CDP 参数 JSON，省略则为空对象 {}")] string? parametersJson = null,
        CancellationToken cancellationToken = default) =>
        session.Host.CdpAsync(
            method,
            string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson,
            cancellationToken);

    [McpServerTool(Name = "close_window")]
    [Description("关闭浏览器窗口并释放 WebView2 资源。MCP 服务本身继续常驻，下次任意工具调用会自动重建窗口（页面状态不保留）。")]
    public async Task<string> CloseWindow(CancellationToken cancellationToken)
    {
        var closed = await session.Host.CloseBrowserAsync(cancellationToken);
        return closed
            ? "浏览器窗口已关闭；服务仍在运行，下次调用工具时会重建窗口"
            : "当前没有打开的浏览器窗口";
    }

    [McpServerTool(Name = "click")]
    [Description("点击匹配 CSS 选择器的元素：先把它滚动到可见，再在元素中心派发真实的鼠标事件（mouseMoved + mousePressed + mouseReleased）。")]
    public async Task<string> Click(
        [Description("CSS 选择器，例如 \"#su\" 或 \"button.submit\"")] string selector,
        [Description("可选：left / right / middle，默认 left")] string button = "left",
        [Description("可选：点击次数，默认 1；2 表示双击")] int clickCount = 1,
        CancellationToken cancellationToken = default)
    {
        var normalizedButton = button.ToLowerInvariant() switch
        {
            "right" => "right",
            "middle" => "middle",
            _ => "left",
        };
        var buttonsMask = normalizedButton switch { "right" => 2, "middle" => 4, _ => 1 };

        var (x, y, description) = await ResolveClickTargetAsync(selector, cancellationToken);

        await session.Host.CdpAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mouseMoved", x, y }),
            cancellationToken);

        await session.Host.CdpAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = normalizedButton, clickCount, buttons = buttonsMask }),
            cancellationToken);

        await session.Host.CdpAsync(
            "Input.dispatchMouseEvent",
            JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = normalizedButton, clickCount, buttons = 0 }),
            cancellationToken);

        return $"已点击 {description}（中心 {x:0},{y:0}，{normalizedButton}，{clickCount} 次）";
    }

    [McpServerTool(Name = "type")]
    [Description("向输入框输入文本：底层是 Input.insertText，会派发真实的输入事件，所以对 React/Vue 这类受控组件同样有效（直接改 value 则无效）。")]
    public async Task<string> Type(
        [Description("要输入的文本")] string text,
        [Description("可选 CSS 选择器；给定时会先聚焦该元素，省略则输入到当前焦点元素")] string? selector = null,
        [Description("可选：先全选原有内容，让新文本替换它，默认 false（追加）")] bool clear = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(selector))
        {
            var selectorLiteral = JsonSerializer.Serialize(selector);
            var clearLiteral = clear ? "true" : "false";
            var focusExpression = $$"""
                (() => {
                  const el = document.querySelector({{selectorLiteral}});
                  if (!el) return null;
                  el.focus();
                  try {
                    if ({{clearLiteral}} && typeof el.select === 'function') el.select();
                    else if (typeof el.setSelectionRange === 'function') el.setSelectionRange(el.value.length, el.value.length);
                  } catch (e) { /* 部分 input 类型不支持 setSelectionRange */ }
                  return el.tagName;
                })()
                """;

            var focused = await EvaluateJsonAsync(focusExpression, cancellationToken);
            if (focused.ValueKind == JsonValueKind.Null)
                throw new InvalidOperationException($"没有元素匹配选择器：{selector}");
        }
        else if (clear)
        {
            await EvaluateJsonAsync(
                "(() => { const el = document.activeElement; if (el && typeof el.select === 'function') el.select(); return true; })()",
                cancellationToken);
        }

        await session.Host.CdpAsync("Input.insertText", JsonSerializer.Serialize(new { text }), cancellationToken);

        var after = await EvaluateJsonAsync(
            "(() => { const el = document.activeElement; if (!el) return null; return typeof el.value === 'string' ? el.value : null; })()",
            cancellationToken);

        if (after.ValueKind != JsonValueKind.String) return "已输入文本（当前焦点元素没有可读的 value）";

        var current = after.GetString() ?? string.Empty;
        return current.Length <= 200
            ? $"当前值：{current}"
            : $"当前值：{current[..200]}…（共 {current.Length} 字符）";
    }

    [McpServerTool(Name = "press")]
    [Description("发送一次按键（keyDown + keyUp）到当前焦点元素。常用键：Enter、Tab、Escape、Backspace、Delete、ArrowUp/Down/Left/Right、Home、End、PageUp、PageDown、Space，也支持单个字符。")]
    public async Task<string> Press(
        [Description("键名，例如 Enter / Tab / Escape / ArrowDown，或单个字符如 a")] string key,
        CancellationToken cancellationToken = default)
    {
        var (code, virtualKeyCode, text) = ResolveKey(key);

        var keyDown = new Dictionary<string, object?>
        {
            ["type"] = "keyDown",
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = virtualKeyCode,
            ["nativeVirtualKeyCode"] = virtualKeyCode,
        };
        if (text is not null)
        {
            keyDown["text"] = text;
            keyDown["unmodifiedText"] = text;
        }

        await session.Host.CdpAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(keyDown), cancellationToken);

        var keyUp = new Dictionary<string, object?>
        {
            ["type"] = "keyUp",
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = virtualKeyCode,
            ["nativeVirtualKeyCode"] = virtualKeyCode,
        };

        await session.Host.CdpAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(keyUp), cancellationToken);

        return $"已发送按键 {key}";
    }

    /// <summary>把选择器解析成元素中心的视口坐标；元素不存在或尺寸为 0 时抛异常。</summary>
    private async Task<(double X, double Y, string Description)> ResolveClickTargetAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        var selectorLiteral = JsonSerializer.Serialize(selector);
        var expression = $$"""
            (() => {
              const el = document.querySelector({{selectorLiteral}});
              if (!el) return null;
              el.scrollIntoView({ block: 'center', inline: 'center' });
              const r = el.getBoundingClientRect();
              if (r.width === 0 || r.height === 0) return { invisible: true, tag: el.tagName, id: el.id || null };
              return {
                x: r.left + r.width / 2,
                y: r.top + r.height / 2,
                tag: el.tagName,
                id: el.id || null,
                text: (el.textContent || '').trim().replace(/\s+/g, ' ').slice(0, 40),
              };
            })()
            """;

        var value = await EvaluateJsonAsync(expression, cancellationToken);

        if (value.ValueKind == JsonValueKind.Null)
            throw new InvalidOperationException($"没有元素匹配选择器：{selector}");

        if (value.TryGetProperty("invisible", out _))
            throw new InvalidOperationException($"元素存在但尺寸为 0（不可见），无法点击：{selector}");

        var x = value.GetProperty("x").GetDouble();
        var y = value.GetProperty("y").GetDouble();

        var tag = value.TryGetProperty("tag", out var tagElement) ? tagElement.GetString() : null;
        var id = value.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        var text = value.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
            ? textElement.GetString()
            : null;

        var description = string.IsNullOrEmpty(id) ? tag ?? selector : $"{tag}#{id}";
        if (!string.IsNullOrEmpty(text)) description += $"（文本：{text}）";

        return (x, y, description);
    }

    /// <summary>把键名映射成 CDP 需要的 code / 虚拟键码 / 文本。</summary>
    private static (string Code, int VirtualKeyCode, string? Text) ResolveKey(string key)
    {
        var known = new Dictionary<string, (string Code, int Vk, string? Text)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = ("Enter", 13, "\r"),
            ["Tab"] = ("Tab", 9, "\t"),
            ["Escape"] = ("Escape", 27, null),
            ["Backspace"] = ("Backspace", 8, null),
            ["Delete"] = ("Delete", 46, null),
            ["ArrowUp"] = ("ArrowUp", 38, null),
            ["ArrowDown"] = ("ArrowDown", 40, null),
            ["ArrowLeft"] = ("ArrowLeft", 37, null),
            ["ArrowRight"] = ("ArrowRight", 39, null),
            ["Home"] = ("Home", 36, null),
            ["End"] = ("End", 35, null),
            ["PageUp"] = ("PageUp", 33, null),
            ["PageDown"] = ("PageDown", 34, null),
            ["Space"] = ("Space", 32, " "),
        };

        if (known.TryGetValue(key, out var entry)) return entry;

        if (key.Length == 1)
        {
            var character = key[0];
            var upper = char.ToUpperInvariant(character);
            var virtualKeyCode = upper is >= 'A' and <= 'Z' ? (int)upper : (int)character;

            var code = character switch
            {
                >= 'a' and <= 'z' => $"Key{upper}",
                >= 'A' and <= 'Z' => $"Key{character}",
                >= '0' and <= '9' => $"Digit{character}",
                ' ' => "Space",
                '-' => "Minus",
                '=' => "Equal",
                ',' => "Comma",
                '.' => "Period",
                '/' => "Slash",
                ';' => "Semicolon",
                '\'' => "Quote",
                '[' => "BracketLeft",
                ']' => "BracketRight",
                '\\' => "Backslash",
                '`' => "Backquote",
                _ => string.Empty,
            };

            return (code, virtualKeyCode, key);
        }

        throw new InvalidOperationException(
            $"不认识的键名：{key}。可用常用键名（Enter/Tab/Escape/ArrowDown…）或单个字符。");
    }

    /// <summary>执行 JS 并把返回值当 JSON 取回；页面抛错或没有可序列化返回值时抛异常。</summary>
    private async Task<JsonElement> EvaluateJsonAsync(string expression, CancellationToken cancellationToken)
    {
        var raw = await session.Host.CdpAsync(
            "Runtime.evaluate",
            JsonSerializer.Serialize(new { expression, returnByValue = true, awaitPromise = true }),
            cancellationToken);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        if (root.TryGetProperty("exceptionDetails", out var exception))
            throw new InvalidOperationException("页面 JS 抛错：" + exception.GetRawText());

        if (root.TryGetProperty("result", out var result) && result.TryGetProperty("value", out var value))
            return value.Clone();

        throw new InvalidOperationException("表达式没有返回可序列化的值：" + raw);
    }

    private static string SummarizeEvaluation(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        if (root.TryGetProperty("exceptionDetails", out var exception))
            return "执行时抛错：\n" + exception.GetRawText();

        if (!root.TryGetProperty("result", out var result)) return raw;

        if (result.TryGetProperty("value", out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

        // 对象/函数没有可序列化的 value，退回 description（例如 "Object"、"function () {...}"）。
        if (result.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
            return description.GetString()!;

        if (result.TryGetProperty("type", out var type))
            return $"（无返回值，类型 {type.GetString()}）";

        return raw;
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) name = $"shot-{DateTime.Now:yyyyMMdd-HHmmss}";
        return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";
    }
}
