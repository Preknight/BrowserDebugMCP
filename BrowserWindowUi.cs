using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BrowserDebugMCP;

/// <summary>窗口内 UI 需要宿主提供的动作；由 <see cref="WebViewHost"/> 实现。</summary>
internal interface IBrowserWindowActions
{
    string ActiveUrl { get; }

    void ActivateTab(BrowserTab tab);

    void CloseTab(BrowserTab tab);

    void CloseActiveTab();

    void OpenNewTab();

    void NavigateFromAddressBar(string text);

    void GoBack();

    void GoForward();

    void ReloadActiveTab(bool ignoreCache);

    void OpenDevTools();

    void FocusActivePage();
}

/// <summary>
/// 浏览器窗口的"外壳"：标签条 + 工具栏 + 内容容器。
///
/// 只负责视图与输入，不碰 WebView2 的 CDP 状态——所有动作都回调给
/// <see cref="IBrowserWindowActions"/>，这样 <see cref="WebViewHost"/> 仍是
/// 唯一知道标签生命周期的地方。
///
/// 快捷键有两条入口，各管一半：
/// - <see cref="Window.PreviewKeyDown"/>：焦点在地址栏 / 工具栏上时，F5、F12、Ctrl+T/W/L、Alt+←/→ 都走这里；
/// - 焦点在网页里时走 WebView2 自己的浏览器快捷键（<c>AreBrowserAcceleratorKeysEnabled</c>），
///   因为网页是独立的原生子窗口（airspace），键盘消息不会冒泡到 WPF 窗口，
///   而 WPF 版的 WebView2 控件并不提供 WinForms 那个 AcceleratorKeyPressed 事件。
/// </summary>
internal sealed class BrowserWindowUi
{
    private static readonly Brush ChromeBackground = Freeze(Color.FromRgb(0xF3, 0xF3, 0xF3));
    private static readonly Brush TabStripBackground = Freeze(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly Brush ActiveTabBackground = Freeze(Colors.White);
    private static readonly Brush ChromeBorder = Freeze(Color.FromRgb(0xCF, 0xCF, 0xCF));

    private readonly IBrowserWindowActions _actions;
    private readonly StackPanel _tabStrip;
    private readonly TextBox _address;
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _reload;
    private readonly Grid _contentHost;

    public BrowserWindowUi(IBrowserWindowActions actions, double width, double height)
    {
        _actions = actions;

        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal };

        var tabScroll = new ScrollViewer
        {
            Content = _tabStrip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var newTabButton = MakeChromeButton("＋", "新建标签页 (Ctrl+T)");
        newTabButton.Click += (_, _) => _actions.OpenNewTab();

        var tabRow = new DockPanel { LastChildFill = true, Background = TabStripBackground };
        DockPanel.SetDock(newTabButton, Dock.Right);
        tabRow.Children.Add(newTabButton);
        tabRow.Children.Add(tabScroll);

        _back = MakeChromeButton("◀", "后退 (Alt+←)");
        _forward = MakeChromeButton("▶", "前进 (Alt+→)");
        _reload = MakeChromeButton("⟳", "刷新 (F5；Ctrl+F5 绕过缓存)");

        _back.Click += (_, _) => _actions.GoBack();
        _forward.Click += (_, _) => _actions.GoForward();
        _reload.Click += (_, _) => _actions.ReloadActiveTab(IsReloadWithoutCache());

        _address = new TextBox
        {
            Height = 26,
            FontSize = 13,
            Padding = new Thickness(6, 3, 6, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            ToolTip = "输入地址后按 Enter；Esc 还原当前地址",
        };
        _address.KeyDown += OnAddressKeyDown;
        _address.GotKeyboardFocus += (_, _) => _address.SelectAll();

        var toolbar = new DockPanel { LastChildFill = true, Margin = new Thickness(6, 4, 6, 6) };
        foreach (var button in new[] { _back, _forward, _reload })
        {
            DockPanel.SetDock(button, Dock.Left);
            toolbar.Children.Add(button);
        }

        toolbar.Children.Add(_address);

        var chrome = new StackPanel { Orientation = Orientation.Vertical, Background = ChromeBackground };
        chrome.Children.Add(tabRow);
        chrome.Children.Add(toolbar);
        chrome.Children.Add(new Border { Height = 1, Background = ChromeBorder });

        _contentHost = new Grid { Background = Brushes.White };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(chrome, 0);
        Grid.SetRow(_contentHost, 1);
        root.Children.Add(chrome);
        root.Children.Add(_contentHost);

        Window = new Window
        {
            Title = "BrowserDebugMCP — 正在初始化浏览器",
            Width = width,
            Height = height,
            Content = root,
            Background = Brushes.White,
            // 与 resize_window 的下限保持一致，否则工具把窗口调小后会被 WPF 又撑回去。
            MinWidth = 200,
            MinHeight = 120,
            UseLayoutRounding = true,
        };

        Window.PreviewKeyDown += OnWindowPreviewKeyDown;

        SetNavigationState(false, false);
        SetAddress("about:blank");
    }

    public Window Window { get; }

    /// <summary>所有标签的 WebView2 都挂在这里；同一时刻只有活动标签是 Visible。</summary>
    public void AttachContent(UIElement element) => _contentHost.Children.Add(element);

    public void DetachContent(UIElement element) => _contentHost.Children.Remove(element);

    /// <summary>重绘标签条。标签数量少，整条重建比增量更新更不容易出错。</summary>
    public void RenderTabStrip(IReadOnlyList<BrowserTab> tabs, BrowserTab? active)
    {
        _tabStrip.Children.Clear();
        foreach (var tab in tabs)
        {
            _tabStrip.Children.Add(BuildTabItem(tab, ReferenceEquals(tab, active)));
        }
    }

    /// <summary>同步地址栏。用户正在地址栏里打字时不覆盖，否则光标会被打回开头。</summary>
    public void SetAddress(string url)
    {
        if (_address.IsKeyboardFocusWithin) return;
        _address.Text = string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : url;
    }

    public void SetNavigationState(bool canGoBack, bool canGoForward)
    {
        _back.IsEnabled = canGoBack;
        _forward.IsEnabled = canGoForward;
        _reload.IsEnabled = true;
    }

    public void FocusAddressBar()
    {
        _address.Focus();
        _address.SelectAll();
    }

    /// <summary>
    /// 统一的快捷键处理。返回 true 表示已处理，调用方要把事件吃掉，
    /// 否则 WebView2 自己还会再做一遍（例如 F12 打开两个 DevTools 窗口）。
    /// </summary>
    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        var control = (modifiers & ModifierKeys.Control) != 0;
        var alt = (modifiers & ModifierKeys.Alt) != 0;

        switch (key)
        {
            case Key.F5:
                // Ctrl+F5 / Shift+F5：绕过缓存，与浏览器习惯一致。
                _actions.ReloadActiveTab(IsReloadWithoutCache());
                return true;

            case Key.F12:
                _actions.OpenDevTools();
                return true;

            case Key.T when control:
                _actions.OpenNewTab();
                return true;

            case Key.W when control:
                _actions.CloseActiveTab();
                return true;

            case Key.L when control:
                FocusAddressBar();
                return true;

            case Key.Left when alt:
                _actions.GoBack();
                return true;

            case Key.Right when alt:
                _actions.GoForward();
                return true;

            default:
                return false;
        }
    }

    private static bool IsReloadWithoutCache() =>
        (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleShortcut(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers))
            e.Handled = true;
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                _actions.NavigateFromAddressBar(_address.Text);
                _actions.FocusActivePage();
                e.Handled = true;
                break;

            case Key.Escape:
                _address.Text = string.Equals(_actions.ActiveUrl, "about:blank", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : _actions.ActiveUrl;
                _actions.FocusActivePage();
                e.Handled = true;
                break;
        }
    }

    private UIElement BuildTabItem(BrowserTab tab, bool isActive)
    {
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(tab.Title) ? "新标签页" : tab.Title,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = Brushes.Black,
        };

        var close = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "关闭标签页 (Ctrl+W)",
        };
        // 关闭按钮自己吃掉点击：否则会先激活这个标签再关掉它，视觉上像闪了一下。
        close.Click += (_, e) =>
        {
            e.Handled = true;
            _actions.CloseTab(tab);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(title);
        row.Children.Add(close);

        var border = new Border
        {
            Child = row,
            Padding = new Thickness(8, 5, 6, 5),
            Margin = new Thickness(0, 2, 2, 0),
            CornerRadius = new CornerRadius(4, 4, 0, 0),
            Background = isActive ? ActiveTabBackground : Brushes.Transparent,
            BorderBrush = isActive ? ChromeBorder : Brushes.Transparent,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Cursor = Cursors.Hand,
            ToolTip = $"{tab.Title}\n{tab.Url}",
        };
        border.MouseLeftButtonDown += (_, _) => _actions.ActivateTab(tab);

        return border;
    }

    private static Button MakeChromeButton(string content, string toolTip) => new()
    {
        Content = content,
        Width = 30,
        Height = 26,
        FontSize = 13,
        Margin = new Thickness(0, 0, 4, 0),
        Padding = new Thickness(0),
        Background = Brushes.White,
        BorderBrush = ChromeBorder,
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        ToolTip = toolTip,
    };

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
