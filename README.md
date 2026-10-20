# BrowserDebugMCP

把 **WebView2** 包装成 MCP 工具的 stdio 服务器：让 MCP 客户端（Reasonix、Claude Desktop、VS Code 等）能直接打开、操作并观察本地网页，用于前端调试与自动化验收。

**不依赖 Node、npm、Playwright 或任何浏览器自动化框架**——用的是 WebView2 内建的 DevTools Protocol 客户端（`CallDevToolsProtocolMethodAsync` + `GetDevToolsProtocolEventReceiver`），纯 .NET。

## 为什么不用 Playwright MCP / chrome-devtools-mcp

| 场景 | 本项目的优势 |
| --- | --- |
| 不想引入 Node 生态 | 那类方案普遍依赖 `npx`；这里只需要 .NET SDK + WebView2 Runtime |
| 调试结果要贴近日常浏览器 | 复用系统已安装的 WebView2 Runtime，它与 Edge 共用同一套 Chromium 引擎，渲染行为一致 |
| 不想额外下发浏览器 | 不捆绑 Chromium，直接用系统运行时 |
| 需要极简部署 | 单 exe，零外部运行时依赖（.NET 除外） |

代价：它不是一个独立浏览器进程，扩展、部分企业策略、以及 `Target` 域的少数能力与真实 Edge 不同；这类需求需要自己用 `cdp` 工具直通。

## 前置条件

- **.NET 10 SDK**
- **WebView2 Runtime**（Windows 11 自带；Windows 10 通常随 Edge 安装，也可从微软官网单独安装）
- 首次构建需要联网，从 nuget.org 还原 3 个包

## 构建

```bash
git clone https://github.com/<your-account>/BrowserDebugMCP.git
cd BrowserDebugMCP
dotnet build -c Release
```

产物：`bin\Release\net10.0-windows\BrowserDebugMCP.exe`

## 接入 MCP 客户端

工具的暴露形式是 `mcp__<server>__<tool>`；服务器以 **stdio** 方式运行，配置里指向**编译好的 exe**。

```toml
# 以 Reasonix 的 reasonix.toml 为例
[[plugins]]
name    = "browser"
command = "C:\\path\\to\\BrowserDebugMCP\\bin\\Release\\net10.0-windows\\BrowserDebugMCP.exe"
```

等价的 `.mcp.json`：

```json
{
  "mcpServers": {
    "browser": {
      "command": "C:\\path\\to\\BrowserDebugMCP\\bin\\Release\\net10.0-windows\\BrowserDebugMCP.exe"
    }
  }
}
```

> **必须指向编译好的 exe，不要写 `dotnet run`**：重新构建时 msbuild 会把输出漏进 stdout，而 stdout 正是 MCP 的 JSON-RPC 通道，连接会当场断掉。

改了代码重新构建后不需要重写配置（路径不变），但**已连接的会话仍持有旧进程**，要重启会话才会用上新版本。

## 工具

| 工具 | 作用 | 底层 CDP |
| --- | --- | --- |
| `open` | 打开 URL 并等待 load 事件（最多 20 秒） | `Page.navigate` + `Page.loadEventFired` |
| `reload` | 重载页面，可选绕过缓存 | `Page.reload` |
| `evaluate` | 执行 JS 表达式并返回结果（自动等 Promise） | `Runtime.evaluate` |
| `click` | 点击匹配 CSS 选择器的元素（先滚动到可见，再点其中心） | `Input.dispatchMouseEvent` |
| `type` | 向输入框输入文本（真实输入事件，受控组件也认） | `Input.insertText` |
| `press` | 发送一次按键（Enter/Tab/Escape/方向键，或单个字符） | `Input.dispatchKeyEvent` |
| `console` | 读累积的 console 输出、未捕获异常、浏览器告警 | `Runtime.consoleAPICalled`、`Runtime.exceptionThrown`、`Log.entryAdded` |
| `network` | 读累积的请求/响应/失败记录 | `Network.requestWillBeSent`、`responseReceived`、`loadingFailed` |
| `dom` | 取整个文档或某个选择器的 outerHTML | `DOM.getDocument`、`DOM.querySelector`、`DOM.getOuterHTML` |
| `screenshot` | 截图存为 PNG，返回路径 | `Page.captureScreenshot` |
| `close_window` | 关闭浏览器窗口并释放 WebView2 资源（服务继续常驻） | 关闭窗口本身，不涉及 CDP |
| `cdp` | **逃生舱**：原样调用任意 CDP 方法 | `CallDevToolsProtocolMethodAsync` |

`cdp` 是兜底手段：任何没有单独封装的能力（`Accessibility.getFullAXTree`、`Performance.getMetrics`、`Emulation.setDeviceMetricsOverride`……）都能直接调用。

## 典型用法

1. 起你的本地站点，记下端口。
2. `open("http://localhost:<port>/")`。
3. `console()` 看有没有报错，`network()` 看资源有没有 404。
4. `evaluate("...")` 探页面状态，`dom("#app")` 看真实渲染结构。
5. `click("#submit")` / `type("hello", "#kw")` / `press("Enter")` 走一遍交互。
6. `screenshot()` 确认视觉效果。
7. 改了前端文件后 `reload()` 再看。

## 运行时行为

### 两层生命周期（刻意设计）

| 层 | 生命周期 |
| --- | --- |
| **MCP 服务进程** | **常驻**。多数客户端一发现它退出就会自动重连（等于重新拉起进程），所以绝不能让"关窗口"去结束进程——否则窗口会关掉又自己冒出来。 |
| **浏览器窗口** | **按需**。进程启动时不建；第一次工具调用才建；关掉它就彻底销毁、WebView2 资源一并释放；下次任意工具调用再重建（**页面状态不保留**）。 |

由此得到的行为：连上服务器**不会**弹窗；手动关掉窗口后它**不会**自己回来；下一次调用工具时窗口才重新出现。想主动收工就用 `close_window`。

- 浏览器建起来后会同时出现**两个窗口**：控制台（stderr 日志）和 WebView2 窗口。后者让截图最可靠。
- 浏览器数据目录：**实测落在 exe 同级的 `BrowserDebugMCP.exe.WebView2\`**（WebView2 在 WPF 宿主下的默认位置）。代码里虽然显式调了 `CoreWebView2Environment.CreateAsync(userDataFolder: ...)`，但该参数在当前 WPF 路径下**未生效**，所以 `BROWSER_DEBUG_MCP_USER_DATA` 实际不起作用。好在默认位置同样在 exe 同级，不会碰你日常浏览器的 profile。
- 截图目录：exe 同级 `screenshots\`，可用 `BROWSER_DEBUG_MCP_SCREENSHOT_DIR` 改写（这一项有效）。
- 两个目录都在 `bin\...` 里面，`dotnet clean` 会一并清掉；需要留存截图请用上面的环境变量改到别处。

## 代码里已经规避的坑

1. **stdout 是协议通道**：所有日志经 `LogToStandardErrorThreshold` 导到 stderr，否则 JSON-RPC 流被污染。
2. **`GetDevToolsProtocolEventReceiver` 的 GC 崩溃**：receiver 必须长期持有引用，内联订阅会在回调触发时随机崩溃——见 `WebViewHost._receivers`。
3. **事件必须先 enable 对应 domain**：只订阅不 enable 收不到任何事件。
4. **初始化不能用带就绪等待的调用**：会与内部 `ready` 互相等待而死锁，所以初始化走内部 `CallCoreAsync`。
5. **导航等待的竞态**：先挂 `TaskCompletionSource` 再发导航，并带 20 秒超时兜底。
6. **UI 线程亲和**：所有 WebView2 访问都经 `Dispatcher.InvokeAsync` marshal 到 STA 线程。
7. **初始化必须排在事件循环之后**：`Show()` 之后直接跑初始化会抛 `InvalidOperationException: EnsureCoreWebView2Async cannot be used before the application's event loop has started running`。必须用 `Dispatcher.InvokeAsync` 把委托排队，等 `Dispatcher.Run()` 开始处理消息后才执行。
8. **WPF 的 WebView2 不会自己初始化**：不给 `Source` 赋值时必须显式调用 `EnsureCoreWebView2Async()`；只订阅 `CoreWebView2InitializationCompleted` 会永远等不到事件（实测踩过：工具调用直接挂死）。
9. **失败必须写日志**：异常只塞进 `TaskCompletionSource` 的话，客户端只显示一句笼统的 `An error occurred invoking ...`，根本无法定位——所以每条 catch 都往 stderr 写完整异常。

## 目前**没有**覆盖的能力

- **iframe / Web Worker**：它们是独立的 CDP target，需要 `Target.setAutoAttach(flatten: true)` 配合 `CallDevToolsProtocolMethodForSessionAsync`。现在的 `cdp` 逃生舱走主 target，进不去子 target。
- **JS 断点 / 单步调试**：需要 `Debugger` 域的完整会话管理。
- **`type` 不产生 `keydown`/`keypress`**：它用 `Input.insertText`，只有 `input` 事件。监听 `keydown` 的组件（自动补全、输入限流）可能不响应——那种场景用 `press` 逐字符发送。
- **`click` 只接受 CSS 选择器**，不支持按坐标点击；需要坐标就用 `cdp` 调 `Input.dispatchMouseEvent`。
- **`press` 未映射组合键**（Ctrl/Shift/Alt 修饰符），需要时用 `cdp` 直通。
- **不是真实 Edge 进程**：扩展、部分企业策略、`Target` 域的部分能力不同（渲染引擎同版本）。
- **窗口被最小化或完全遮挡时**，`Page.captureScreenshot` 可能返回空白或旧帧。

## 验证状态

构建与安装：

- `dotnet build -c Release` → **0 警告 / 0 错误**。
- 作为 MCP 服务器安装后，客户端枚举到 **12 个工具**，全部可用。

端到端实调（经 MCP 通道，非本地模拟）：

| 调用 | 结果 |
| --- | --- |
| `evaluate("navigator.userAgent")` | `… Chrome/153.0.0.0 Safari/537.36 Edg/153.0.0.0` |
| `evaluate("console.log(…); document.title='probe-ok'")` | 返回 `probe-ok` |
| `console()` | `[log] …`、`[warning] …` —— console 捕获有效 |
| `dom()` | 返回完整的 `<html>…</html>` |
| `screenshot()` | 生成有效 PNG |
| `type("…", "#chat-textarea", clear=true)` | 返回输入框当前值，中文正常 |
| `press("Enter")` | 返回成功，且页面 URL 带上了 `wd=` 参数（搜索真的触发） |
| `click("#probe-target")` | 页面内 `addEventListener('click')` 计数器读到 `1`，`event.target` 是坐标点上的实际元素 |
| `close_window` | 窗口销毁、服务进程存活；再次调用工具时窗口重建并照常工作 |

未逐一实调：`open` / `reload` / `network` / `cdp`——它们与上面几个走同一条 CDP 通道。

## 文件

| 文件 | 职责 |
| --- | --- |
| `BrowserDebugMCP.csproj` | `net10.0-windows` + `UseWPF`，三个包引用 |
| `Program.cs` | MCP host、日志导 stderr、目录派生 |
| `WebViewHost.cs` | STA UI 线程、WebView2 生命周期、CDP 调用与事件订阅、导航等待 |
| `ConsoleBuffer.cs` | console / 异常 / 浏览器告警的累积与渲染 |
| `NetworkBuffer.cs` | 网络请求的累积与渲染 |
| `BrowserTools.cs` | 12 个 MCP 工具 |
| `BrowserSession.cs` | 工具类注入用的状态聚合 |

## 已知限制与后续

- `[McpServerTool]` 的 `ReadOnly`/`Idempotent` 等 hint 属性未设置（默认"可写、非破坏"）。若希望 `console`/`network`/`dom` 参与并行调度或只读子代理，可给这三个方法加 `ReadOnly = true`。
- License 未指定。
