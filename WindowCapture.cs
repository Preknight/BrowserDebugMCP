using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrowserDebugMCP;

/// <summary>
/// 截图的两条腿：WPF 自己渲染 chrome，Win32 抓网页，再合成成 PNG。
///
/// 为什么必须两边各取所长：
/// - WPF 的 WebView2 由 <c>HwndHost</c> 承载，网页是独立原生子窗口（airspace），
///   <c>RenderTargetBitmap</c> 只渲染 WPF 视觉树，网页区域会是空白；
/// - 反过来，顶层窗口的 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 能拿到 Chromium 那块，
///   但 WPF 用 D3D 画的内容（标签条、工具栏）拿不到。
/// </summary>
internal static class WindowCapture
{
    private const uint PwRenderFullContent = 0x00000002;

    // SelectObject 失败时返回 NULL，(HGDIOBJ)-1 表示区域对象相关的错误。
    private static readonly IntPtr HgdiError = new(-1);

    internal readonly record struct Result(byte[] Png, int Width, int Height, string Method);

    /// <summary>
    /// 抓"客户区完整画面"：WPF 的 chrome（标签条 + 工具栏）+ WebView2 里的网页。
    ///
    /// 为什么需要单独一条路径：加标签条/工具栏之后，顶层窗口的 PrintWindow 只能拿到
    /// DWM 合成结果里 Chromium 那部分，WPF 自己用 D3D 画的内容会缺失（截出来是桌面底色）；
    /// 而 RenderTargetBitmap 又渲染不了 WebView2（HwndHost / airspace，网页区域空白）。
    /// 所以这里各取所长：RTB 画 chrome，PrintWindow 取网页，再按控件在客户区里的偏移拼起来。
    ///
    /// 输出不含窗口标题栏，尺寸按客户区的物理像素。
    /// </summary>
    public static Result CaptureComposite(FrameworkElement root, FrameworkElement webView, IntPtr webViewHandle)
    {
        var dpi = VisualTreeHelper.GetDpi(root);
        var width = (int)Math.Round(root.ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Round(root.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"客户区尺寸无效：{width}×{height}");

        // chrome 先渲染成位图：DrawingVisual 里没法直接画"视觉树的渲染结果"。
        var chrome = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        chrome.Render(root);

        BitmapSource? page = null;
        var method = "WPF 渲染（chrome；网页抓取失败，该区域为空白）";

        if (webViewHandle != IntPtr.Zero
            && TryGetWindowSize(webViewHandle, out var pageWidth, out var pageHeight)
            && TryPrintWindow(webViewHandle, pageWidth, pageHeight, out var pagePixels)
            && !LooksBlank(pagePixels))
        {
            page = BitmapSource.Create(
                pageWidth, pageHeight, 96, 96, PixelFormats.Bgra32, null,
                WithOpaqueAlpha(pagePixels), pageWidth * 4);
            page.Freeze();
            method = "WPF 渲染（chrome）+ PrintWindow（网页）";
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(chrome, new Rect(0, 0, root.ActualWidth, root.ActualHeight));

            if (page is not null)
            {
                // WebView2 控件在客户区里的位置（DIP），与 chrome 位图同一坐标系。
                var offset = webView.TranslatePoint(new Point(0, 0), root);
                dc.DrawImage(page, new Rect(offset.X, offset.Y, webView.ActualWidth, webView.ActualHeight));
            }
        }

        var composite = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        composite.Render(visual);
        composite.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composite));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new Result(stream.ToArray(), width, height, method);
    }

    private static bool TryGetWindowSize(IntPtr hwnd, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!GetWindowRect(hwnd, out var rect)) return false;

        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return width > 0 && height > 0;
    }

    private static bool TryPrintWindow(IntPtr hwnd, int width, int height, out byte[] pixels)
    {
        pixels = [];

        // 位图格式必须取自屏幕 DC：CreateCompatibleDC(NULL) 的内存 DC 默认只选着 1×1 单色位图，
        // 拿它当 CreateCompatibleBitmap 的格式来源会得到 1bpp 位图，PrintWindow 的结果就变成黑白失真图。
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        var hdc = CreateCompatibleDC(screenDc);
        if (hdc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(hdc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        var previous = SelectObject(hdc, bitmap);
        if (previous == IntPtr.Zero || previous == HgdiError)
        {
            DeleteObject(bitmap);
            DeleteDC(hdc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return false;
        }

        try
        {
            if (!PrintWindow(hwnd, hdc, PwRenderFullContent)) return false;
            pixels = ReadPixels(bitmap, width, height);
            return true;
        }
        finally
        {
            SelectObject(hdc, previous);
            DeleteObject(bitmap);
            DeleteDC(hdc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把 HBITMAP 读成 BGRA 字节；先把格式统一成 Bgra32，避免 24bpp 位图让 stride 算错。</summary>
    private static byte[] ReadPixels(IntPtr bitmap, int width, int height)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());

        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    /// <summary>
    /// 判断 PrintWindow 是否只抓到一块黑图。采样而非逐像素，避免大窗口上的额外开销；
    /// 页面本身全黑时只会少贴一次网页位图，不影响 chrome 部分的正确性。
    /// </summary>
    private static bool LooksBlank(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4 * 97)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// GDI 抓来的位图 alpha 不可信（通常是 0），统一置为不透明，
    /// 否则 PNG 保存出来会是全透明的一张图。
    /// </summary>
    private static byte[] WithOpaqueAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return pixels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}
