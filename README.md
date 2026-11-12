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

> 如果客户端正连着上一次构建的 exe，覆盖会失败（`MSB3021 / MSB3027`，文件被 `BrowserDebugMCP` 进程锁定）。先关掉会话或用 `-c Debug` 验证编译，再重启会话让新 exe 生效。

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

| 工具 | 作用 | 底层实现 |
| --- | --- | --- |
| `open` | 打开 URL 并等待 load 事件（最多 20 秒） | `Page.navigate` + `Page.loadEventFired` |
| `reload` | 重载页面，可选绕过缓存 | `Page.reload` |
| `evaluate` | 执行 JS 表达式并返回结果（自动等 Promise） | `Runtime.evaluate` |
| `click` | 点击匹配 CSS 选择器的元素（先滚动到可见，再点其中心） | `Input.dispatchMouseEvent` |
| `type` | 向输入框输入文本（真实输入事件，受控组件也认） | `Input.insertText` |
| `press` | 发送一次按键（Enter/Tab/Escape/方向键，或单个字符） | `Input.dispatchKeyEvent` |
| `console` | 读**活动标签**累积的 console 输出、未捕获异常、浏览器告警 | `Runtime.consoleAPICalled`、`Runtime.exceptionThrown`、`Log.entryAdded` |
| `network` | 读**活动标签**累积的请求/响应/失败记录 | `Network.requestWillBeSent`、`responseReceived`、`loadingFailed` |
| `dom` | 取整个文档或某个选择器的 outerHTML | `DOM.getDocument`、`DOM.querySelector`、`DOM.getOuterHTML` |
| `screenshot` | 截页面渲染帧存为 PNG，返回路径 | `Page.captureScreenshot` |
| `screenshot_window` | 截**客户区完整画面**（标签条 + 工具栏 + 网页）存为 PNG，返回路径与合成方式 | WPF `RenderTargetBitmap` 渲染 chrome + Win32 `PrintWindow` 抓网页，按控件偏移合成（不涉及 CDP） |
| `close_window` | 关闭浏览器窗口并释放 WebView2 资源（服务继续常驻） | 关闭窗口本身，不涉及 CDP |
| `resize_window` | 调整窗口大小（DIP，含顶部标签条与工具栏；省略的一方沿用当前值，两个都不给则只回报尺寸） | 改 WPF 窗口尺寸，不涉及 CDP |
| `cdp` | **逃生舱**：原样调用任意 CDP 方法（作用于活动标签） | `CallDevToolsProtocolMethodAsync` |

两个截图工具的分工：

- `screenshot` 走 CDP，取的是**页面视口渲染帧**——干净、不受窗口遮挡影响，看不到标签条与工具栏，但窗口最小化时可能返回空白或旧帧。
- `screenshot_window` 走 WPF 渲染 + Win32 抓网页再合成，抓的是**客户区完整画面**（含标签条、工具栏，尺寸按物理像素），能反映窗口被遮挡/缩放后的实际样子；窗口最小化时直接报错。它不含窗口标题栏。

`cdp` 是兜底手段：任何没有单独封装的能力（`Accessibility.getFullAXTree`、`Performance.getMetrics`、`Emulation.setDeviceMetricsOverride`……）都能直接调用。

## 窗口 UI：标签页与工具栏

窗口顶部有两条 chrome（WPF 自绘，不属于网页内容）：

| 区域 | 内容 |
| --- | --- |
| 标签条 | 每个标签一个可点击标题（过长截断，悬停显示完整标题与 URL），右侧 `×` 关闭，条末 `＋` 新建 |
| 工具栏 | `◀` 后退、`▶` 前进、`⟳` 刷新、地址栏（显示活动标签的地址） |

**默认页**：`https://www.bing.com/`（会按区域跳到 `cn.bing.com`）。首次建窗口的第一个标签、`＋` / `Ctrl+T` 新建的标签都打开它；工具有明确目标 URL 时**直接**用目标 URL 当初始页，不会先开默认页再跳走。改默认页只需改 `WebViewHost.DefaultHomeUrl`。

### 标签页语义

- **每个标签是一块独立的 WebView2 实例**：页面、JS 状态、前进后退历史互不干扰（`history.length` 各算各的）。
- **同一时刻只显示一个**：非活动标签用 `Visibility.Collapsed` 隐藏，而**不是**移出视觉树 —— `WebView2` 的 WPF 宿主是 `HwndHost`，一旦离开视觉树就会销毁原生窗口、连带使 `CoreWebView2` 失效，等于把那个标签的页面丢掉。
- **MCP 工具没有标签参数**：`open` / `evaluate` / `dom` / `screenshot` / `cdp` / `console` / `network` 全部作用在**活动标签**上。切到哪个标签，工具看到的就是哪个页面。
- `console` / `network` 缓冲**按标签隔离**：读的是活动标签自己的记录，不会混进别的标签。
- 关掉最后一个标签 = 关掉整个浏览器窗口（进程继续常驻，下次调用再重建）。
- 页面里的 `window.open(...)` / `target="_blank"` 会新开一个标签（宿主接管 `NewWindowRequested`）。

### 快捷键

| 按键 | 行为 | 生效范围 |
| --- | --- | --- |
| `F5` | 刷新当前标签 | 网页焦点走 WebView2 原生；工具栏/地址栏焦点走 WPF |
| `Ctrl+F5` / `Shift+F5` | 绕过缓存刷新 | 同上 |
| `F12` | 打开 DevTools 窗口 | 网页焦点走 WebView2 原生；工具栏焦点走 `OpenDevToolsWindow()` |
| `Ctrl+T` / `Ctrl+W` | 新建标签 / 关闭当前标签 | WPF 侧 |
| `Ctrl+L` | 聚焦地址栏并全选 | WPF 侧 |
| `Alt+←` / `Alt+→` | 后退 / 前进 | WPF 侧 |
| `Enter`（地址栏） | 导航；无协议头时 `localhost`/IP 补 `http://`，其余补 `https://` | WPF 侧 |
| `Esc`（地址栏） | 还原为当前地址 | WPF 侧 |

"生效范围"这一列是有原因的：网页是独立原生子窗口（airspace），键盘消息不会冒泡到 WPF 窗口，而 **WPF 版的 `WebView2` 控件并没有 WinForms 那个 `AcceleratorKeyPressed` 事件**，所以网页焦点下的 F5/F12 只能交给 WebView2 自己的浏览器快捷键（`AreBrowserAcceleratorKeysEnabled` 保持开启）；其余组合键只在 WPF 侧（地址栏、工具栏、标签条有焦点时）生效。

## 典型用法

1. 起你的本地站点，记下端口。
2. `open("http://localhost:<port>/")`。
3. `console()` 看有没有报错，`network()` 看资源有没有 404。
4. `evaluate("...")` 探页面状态，`dom("#app")` 看真实渲染结构。
5. `click("#submit")` / `type("hello", "#kw")` / `press("Enter")` 走一遍交互。
6. 需要看别的宽度下的布局就 `resize_window(1440, 900)`（返回里会附上页面实际视口）。
7. `screenshot()` 确认视觉效果；需要窗口本身的样子（含遮挡/缩放）时用 `screenshot_window()`。
8. 改了前端文件后 `reload()` 再看。

## 运行时行为

### 单实例：一个宿主，多个会话共享

同一个登录会话里，无论有多少个 MCP 客户端（Reasonix 会话、子 Agent、其他客户端进程），**只有一个进程真正持有 WebView2**：

| 角色 | 怎么产生 | 行为 |
| --- | --- | --- |
| **宿主** | 第一个启动、抢到命名互斥量 `Local\BrowserDebugMCP.Host` 的进程 | 创建 `WebViewHost`（仍按需懒建窗口），并在命名管道 `BrowserDebugMCP.Host.<用户名>` 上服务 |
| **代理** | 其余进程（互斥量已存在） | 不建浏览器，把每次工具调用经管道转发给宿主；仍是 stdio MCP 服务器，客户端无感 |
| **接管** | 宿主随它所属的会话一起退出后，代理的下一次调用发现连接断开且互斥量已释放 | 该代理升级为宿主并重建浏览器（页面状态丢失） |

所有调用——宿主自己的 MCP 工具调用与所有代理请求——都经过同一把全局串行锁，因此**同一时刻只有一个调用在操作浏览器**。

**多个 Agent 同时使用时的语义**：

- 支持并发调用：请求排队执行，不会出现两个 WebView2，也不会把 CDP 调用搅乱。
- **忙提示**：排队超过 **10 秒**（`BROWSER_DEBUG_MCP_BUSY_TIMEOUT_MS` 可调）仍未轮上时，工具直接返回错误：说明浏览器正被另一个会话占用、已占用多久，并明确要求**不要轮询或连续重试**。这样调用方不会在共享浏览器上反复空转。
- **页面状态不隔离**：一个 Agent 的 `open` 会把浏览器导航走，别的 Agent 随后的 `evaluate` / `dom` / `screenshot` 看到的是别人的页面。窗口里的标签是所有人共用的——别人切了标签，你的下一次调用就会落到另一个页面上。
- 标签本身没有 MCP 工具：新建/切换/关闭只能由人在窗口里操作（或页面自己 `window.open`）。
- `close_window`、`resize_window`、`screenshot_window` 作用在同一个窗口上，对所有 Agent 生效。
- 需要"每个 Agent 独立页面"时不要用单实例共享：那需要按会话分配独立 WebView2（当前未实现）。

### 两层生命周期（刻意设计）

| 层 | 生命周期 |
| --- | --- |
| **MCP 服务进程** | **常驻**。多数客户端一发现它退出就会自动重连（等于重新拉起进程），所以绝不能让"关窗口"去结束进程——否则窗口会关掉又自己冒出来。 |
| **浏览器窗口** | **按需**。进程启动时不建；第一次工具调用才建；关掉它就彻底销毁、WebView2 资源一并释放；下次任意工具调用再重建（**页面状态不保留**）。 |

由此得到的行为：连上服务器**不会**弹窗；手动关掉窗口后它**不会**自己回来；下一次调用工具时窗口才重新出现。想主动收工就用 `close_window`。

- 浏览器建起来后会同时出现**两个窗口**：控制台（stderr 日志）和 WebView2 窗口。后者让截图最可靠。
- 窗口尺寸：默认 **1280×880**（WPF DIP，含窗口边框与顶部 chrome），可用 `resize_window` 调整。调过的尺寸在窗口被关掉重建后**继续沿用**（仅限当前进程）；进程重启则回到默认值。
- 顶部 chrome（标签条 + 工具栏）占掉约 **70 DIP** 的高度：默认尺寸下页面视口约 1266×~740，而不是窗口高度减去边框。`resize_window` 返回里会附上实测视口，需要精确视口就照它校准。
- 浏览器数据目录：**实测落在 exe 同级的 `BrowserDebugMCP.exe.WebView2\`**（WebView2 在 WPF 宿主下的默认位置）。代码里虽然显式调了 `CoreWebView2Environment.CreateAsync(userDataFolder: ...)`，但该参数在当前 WPF 路径下**未生效**，所以 `BROWSER_DEBUG_MCP_USER_DATA` 实际不起作用。好在默认位置同样在 exe 同级，不会碰你日常浏览器的 profile。
- **cookie / 登录态是持久的**（2026-11-12 实测）：`Storage.getCookies` 读出百度登录态（`BDUSS`、`BIDUPSID`）、起点、`127.0.0.1:3510` 的 `dsh-auth` 等；杀掉 `BrowserDebugMCP.exe` 再重新拉起后，这些 cookie 的值逐项一致，只在访问 bing 时新增了 bing 自己的项。也就是说关窗口、重启后端、重新构建都不丢登录态。
- 截图目录：exe 同级 `screenshots\`，可用 `BROWSER_DEBUG_MCP_SCREENSHOT_DIR` 改写（这一项有效）。`screenshot` 生成 `shot-<时间戳>.png`，`screenshot_window` 生成 `window-<时间戳>.png`。
- 排队上限：`BROWSER_DEBUG_MCP_BUSY_TIMEOUT_MS`（默认 `10000`）。多个 Agent 共享浏览器时，等锁超过这个时长会返回“正被其他会话占用、不要轮询”的错误，而不是继续挂着。
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
10. **最大化时改 `Width`/`Height` 不生效**：`resize_window` 先把 `WindowState` 还原成 `Normal` 再设尺寸，否则调用看着成功、窗口纹丝不动。
11. **`RenderTargetBitmap` 抓不到 WebView2**：WPF 的 WebView2 由 `HwndHost` 承载，网页是独立原生子窗口（airspace），RTB 只渲染 WPF 视觉树，网页区域会是空白。
12. **`PrintWindow` 抓不到 WPF 自绘层**：反过来也成立 —— 顶层窗口的 `PrintWindow(PW_RENDERFULLCONTENT)` 能拿到 Chromium 那块，但 WPF 用 D3D 画的标签条/工具栏会缺失（截出来是桌面底色）。所以 `screenshot_window` 用两条路合成：RTB 渲染 chrome + `PrintWindow` 抓网页，按控件在客户区里的偏移拼起来（`WindowCapture.CaptureComposite`）。
13. **`PrintWindow` 必须在其窗口所属 UI 线程调用**：它同步给目标窗口发 `WM_PRINT`，跨线程发同步消息容易把两个线程一起卡死——见 `WebViewHost.CaptureWindowAsync`。
14. **GDI 抓来的位图 alpha 不可信**：`BitBlt` 结果的 alpha 通常是 0，直接编码 PNG 会得到全透明图，所以编码前统一置为不透明。
15. **多标签不能把 WebView2 移出视觉树**：`HwndHost` 离开视觉树会 `DisposeWindowCore` 销毁原生窗口，`CoreWebView2` 随之失效，标签再显示回去等于重建浏览器。所以切换标签只改 `Visibility`（活动 `Visible`、其余 `Collapsed`），页面与 JS 状态都保住。
16. **WPF 版 WebView2 没有 `AcceleratorKeyPressed`**：那是 WinForms 控件的事件。网页焦点下的键盘事件既不会冒泡到 WPF 窗口（airspace），也无法在 WPF 侧拦截，所以 F5/F12 交给 WebView2 自带的浏览器快捷键（`AreBrowserAcceleratorKeysEnabled = true`），其余组合键只在 WPF 侧（地址栏/工具栏/标签条有焦点）生效。
17. **每个标签要有自己的缓冲与 receiver**：`ConsoleBuffer` / `NetworkBuffer` / `GetDevToolsProtocolEventReceiver` 的引用都绑在具体的 `CoreWebView2` 上；共用一份会让不同标签的日志互相污染，receiver 丢了引用还会随机崩。
18. **绝对不能开 `InvariantGlobalization`（含地址栏之后才暴露）**：WPF 的 `TextBox` 在鼠标点击定位光标时走 `TextSelection.EnsureCaret` → `InputLanguageSource.CurrentInputLanguage`，它要构造当前输入语言对应的 `CultureInfo`（中文系统是 2052）。invariant 模式下这会抛 `CultureNotFoundException: 2052 (0x0804) is an invalid culture identifier`，异常从 `HwndSubclass.SubclassWndProc` 一路逃出 `Dispatcher.Run()`，UI 线程结束 —— **表现就是"点一下地址栏窗口就消失了"**，而进程还活着。加地址栏之前没有 `TextBox`，所以这个坑潜伏了很久。工程里显式写 `<InvariantGlobalization>false</InvariantGlobalization>` 并留了注释。
19. **UI 线程要装 `Dispatcher.UnhandledException` 兜底**：`Dispatcher.Run()` 一旦因未处理异常返回，UI 线程就结束了，窗口跟着消失，而 MCP 进程仍在跑（工具调用会重建窗口，看起来像"自己重启了"）。兜底里记日志 + `e.Handled = true`，能让窗口在下一次小异常后继续存活。

## 目前**没有**覆盖的能力

- **iframe / Web Worker**：它们是独立的 CDP target，需要 `Target.setAutoAttach(flatten: true)` 配合 `CallDevToolsProtocolMethodForSessionAsync`。现在的 `cdp` 逃生舱走主 target，进不去子 target。
- **JS 断点 / 单步调试**：需要 `Debugger` 域的完整会话管理。
- **`type` 不产生 `keydown`/`keypress`**：它用 `Input.insertText`，只有 `input` 事件。监听 `keydown` 的组件（自动补全、输入限流）可能不响应——那种场景用 `press` 逐字符发送。
- **`click` 只接受 CSS 选择器**，不支持按坐标点击；需要坐标就用 `cdp` 调 `Input.dispatchMouseEvent`。
- **`press` 未映射组合键**（Ctrl/Shift/Alt 修饰符），需要时用 `cdp` 直通。
- **不是真实 Edge 进程**：扩展、部分企业策略、`Target` 域的部分能力不同（渲染引擎同版本）。
- **窗口最小化/隐藏/完全遮挡时的截图**：`screenshot` 可能返回空白或旧帧；`screenshot_window` 在最小化或隐藏时直接报错，被完全遮挡时抓到的只是遮挡物（`PrintWindow` 对 Chromium 的 GPU 合成层也并非总能成功，会回退 `BitBlt`）。
- **`resize_window` 只改真实窗口外框**（DIP），不模拟设备像素比或移动端视口；要精确控制视口/DPR 用 `cdp` 调 `Emulation.setDeviceMetricsOverride`，也不需要最大化、全屏这类窗口状态。
- **标签页没有 MCP 工具**：新建 / 切换 / 关闭只能在窗口里由人操作（`window.open` 是 Agent 唯一能间接触发新建的方式）。工具永远作用于"当前活动标签"。
- **网页焦点下的 F5/F12 不经过本项目代码**：由 WebView2 内置浏览器快捷键处理，因此不会出现在工具日志里，也不会触发 MCP 侧的 load 等待（只有工具调用 `reload` / `open` 才走那条路）。

## 验证状态

构建与安装：

- `dotnet build -c Debug` → **0 警告 / 0 错误**；`dotnet build -c Release -t:Compile` 同样 **0 警告 / 0 错误**（Release 的 exe 复制被正在运行的 MCP 进程锁定，需重启会话后重跑 `dotnet build -c Release`）。
- 单实例改造（宿主/代理 + 命名管道）同样是 Debug 与 Release Compile 均 **0 警告 / 0 错误**，但**尚未做多进程实调**：需要两个以上会话同时连上，观察日志里的“宿主 / 代理”角色、停掉宿主会话后代理接管，以及并发调用排队。
- 新增 `resize_window` 前，作为 MCP 服务器安装后客户端枚举到 **12 个工具**，全部可用；加入 `resize_window` 与 `screenshot_window` 后为 **14 个**。
- 标签页 + 工具栏改造后仍是 **14 个工具**（没有新增工具），Debug 与 Release 均 **0 警告 / 0 错误**。

标签页与工具栏（2026-11-12，经 MCP 通道 + 合成截图实测）：

| 验证点 | 结果 |
| --- | --- |
| 布局 | `screenshot_window` 的合成截图里标签条（`Example Domain ×`、`＋`）、工具栏（`◀ ▶ ⟳` + 地址栏）与网页同框显示，无误位 |
| 地址栏联动 | `open("https://example.com")` 后地址栏显示 `https://example.com/`；再 `open("…?p=2")` 后变为 `https://example.com/?p=2` |
| 历史按钮 | 首次加载时 `◀` 与 `▶` 均灰显；产生第二条历史后 `◀` 变亮、`▶` 仍灰（`HistoryChanged` → 按钮状态更新生效） |
| 多标签 | 页面内 `window.open('…?tab=2')` 经 `NewWindowRequested` 新建了第二个标签，标签条出现两项且新标签高亮 |
| 标签隔离 | 新标签里 `history.length = 1`（与标签 1 的 2 条历史互不影响）；`evaluate` 读到 `location.href = https://example.com/?tab=2`，证明工具作用于活动标签 |
| 视口 | `resize_window(1200, 820)` → 窗口 1200×820 DIP、页面视口 1186×716（约 70 DIP 被 chrome 占掉） |
| 点地址栏（回归） | 窗口关闭后重建并 `open`，再用 Win32 `PostMessage(WM_LBUTTONDOWN/UP)` 向客户区 (400,58) 投递真实鼠标消息：窗口存活、`hub.log` 零新增异常、截图里地址栏出现插入光标 —— 修复前同一路径会因 `InvariantGlobalization` 抛 `CultureNotFoundException` 并让窗口消失 |
| 默认页 | 只调 `screenshot_window`（带 URL 的工具会覆盖初始页）触发建窗口：`location.href = https://cn.bing.com/`、标题「搜索 - Microsoft 必应」，标签条与地址栏同步显示 |
| cookie 持久化 | 杀进程重启前后各读一次 `Storage.getCookies`：百度 `BDUSS`/`BIDUPSID`/`BAIDUID`、起点、`127.0.0.1:3510` 的 `dsh-auth` 值逐项一致；仅新增 bing 自己的 cookie |

未实调（只能由人操作窗口验证）：用鼠标点标签切换/关闭、`＋` 新建、地址栏键入与 Esc 还原、以及**网页焦点下的 F5 与 F12**（自动化通道发不出真实键盘消息）。

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
| `screenshot_window` | 合成截图（`WPF 渲染（chrome）+ PrintWindow（网页）`），1582×1053 物理像素，chrome 与网页都对得上 |
| `resize_window` | 1200×820 → 视口 1186×716，与 chrome 占位一致 |

未逐一实调：`open` / `reload` / `network` / `cdp`——它们与上面几个走同一条 CDP 通道。

## 文件

| 文件 | 职责 |
| --- | --- |
| `BrowserDebugMCP.csproj` | `net10.0-windows` + `UseWPF`，三个包引用 |
| `Program.cs` | MCP host、单实例角色选择、日志导 stderr、目录派生 |
| `BrowserHost.cs` | `IBrowserHost` 后端接口与宿主业务错误类型 |
| `BrowserGateway.cs` | 单实例角色选举、宿主接管、全局串行锁 |
| `SingleInstanceHost.cs` | 宿主侧命名管道服务端（一连接一处理） |
| `RemoteBrowserHost.cs` | 代理侧命名管道客户端（请求/响应配对、断线重连） |
| `BrowserIpc.cs` | 宿主与代理之间的 JSON 行协议与分发 |
| `WebViewHost.cs` | STA UI 线程、标签集合与活动标签、WebView2 生命周期、窗口尺寸与抓图入口、CDP 调用与导航等待 |
| `BrowserTab.cs` | 单个标签：独立 WebView2/CoreWebView2、自己的 console/network 缓冲、load 等待与前进后退状态 |
| `BrowserWindowUi.cs` | 窗口外壳：标签条、工具栏（后退/前进/刷新/地址栏）、快捷键分发 |
| `WindowCapture.cs` | `PrintWindow` / `BitBlt` 抓像素与"chrome + 网页"合成（→ PNG） |
| `ConsoleBuffer.cs` | console / 异常 / 浏览器告警的累积与渲染 |
| `NetworkBuffer.cs` | 网络请求的累积与渲染 |
| `BrowserTools.cs` | 14 个 MCP 工具 |
| `BrowserSession.cs` | 工具类注入用的状态聚合 |

## 已知限制与后续

- `[McpServerTool]` 的 `ReadOnly`/`Idempotent` 等 hint 属性未设置（默认"可写、非破坏"）。若希望 `console`/`network`/`dom` 参与并行调度或只读子代理，可给这三个方法加 `ReadOnly = true`。
- `screenshot_window` 的 `PrintWindow` 路径对部分 GPU 合成场景可能整块黑，目前只靠"整块黑就回退 `BitBlt`"兜底；更稳的做法是改用 `CoreWebView2.CapturePreviewAsync`（WebView2 官方渲染接口，但拿不到窗口外框）。
- License 未指定。
