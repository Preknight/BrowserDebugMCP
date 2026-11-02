using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BrowserDebugMCP;

/// <summary>
/// 用 Win32 抓取 WebView2 控件窗口的**真实像素**，编码成 PNG。
///
/// 为什么不用 RenderTargetBitmap：WPF 的 WebView2 由 <c>HwndHost</c> 承载，
/// 网页内容是独立的原生子窗口（WPF 的 airspace 限制），而 RenderTargetBitmap 只渲染
/// WPF 视觉树，网页所在区域会是空白 —— 这正是这个类存在的原因。
///
/// 抓取顺序：先 <c>PrintWindow(PW_RENDERFULLCONTENT)</c>（支持被遮挡的窗口，也是
/// Chromium 内容唯一可能被 WM_PRINT 拿到的形式）；失败或抓到整块黑图时，退回从屏幕
/// 直接 <c>BitBlt</c>（要求窗口可见）。
/// </summary>
internal static class WindowCapture
{
    private const uint PwRenderFullContent = 0x00000002;
    private const uint SrcCopy = 0x00CC0020;

    internal readonly record struct Result(byte[] Png, int Width, int Height, string Method);

    /// <summary>抓取窗口。必须在窗口所属的 UI 线程上调用：PrintWindow 会同步给目标窗口发消息。</summary>
    public static Result Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("拿不到 WebView2 的窗口句柄");

        // 子窗口没有"最小化"状态，所以本检查只对顶层句柄有效；
        // 调用方（WebViewHost.CaptureWindowAsync）另外检查宿主 Window 的 WindowState。
        if (IsIconic(hwnd))
            throw new InvalidOperationException("窗口已最小化，截不到画面；请恢复窗口后再调用");

        if (!GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("读取窗口尺寸失败（GetWindowRect）");

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException($"窗口尺寸无效：{width}×{height}");

        if (TryPrintWindow(hwnd, width, height, out var pixels) && !LooksBlank(pixels))
            return new Result(EncodePng(pixels, width, height), width, height, "PrintWindow");

        // 常见于 Chromium 的 GPU 合成层：PrintWindow 返回成功但内容是黑的。
        // 退一步直接拷屏幕像素，代价是窗口必须可见（被完全遮挡时会拷到遮挡物）。
        var blitted = CaptureWithBitBlt(rect, width, height)
            ?? throw new InvalidOperationException("PrintWindow 与屏幕 BitBlt 都失败，无法截取窗口");

        return new Result(EncodePng(blitted, width, height), width, height, "BitBlt（屏幕像素）");
    }

    private static bool TryPrintWindow(IntPtr hwnd, int width, int height, out byte[] pixels)
    {
        pixels = [];

        var hdc = CreateCompatibleDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;

        var bitmap = CreateCompatibleBitmap(hdc, width, height);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(hdc);
            return false;
        }

        var previous = SelectObject(hdc, bitmap);
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
        }
    }

    private static byte[]? CaptureWithBitBlt(Win32Rect rect, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var hdc = CreateCompatibleDC(screenDc);
        if (hdc == IntPtr.Zero)
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(hdc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var previous = SelectObject(hdc, bitmap);
        try
        {
            if (!BitBlt(hdc, 0, 0, width, height, screenDc, rect.Left, rect.Top, SrcCopy)) return null;
            return ReadPixels(bitmap, width, height);
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
    /// 页面本身全黑时只会多走一次 BitBlt，不影响结果正确性。
    /// </summary>
    private static bool LooksBlank(byte[] pixels)
    {
        for (var i = 0; i + 3 < pixels.Length; i += 4 * 97)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) return false;
        }

        return true;
    }

    private static byte[] EncodePng(byte[] pixels, int width, int height)
    {
        // GDI 抓来的位图 alpha 不可信（BitBlt 通常是 0），统一置为不透明，
        // 否则 PNG 保存出来会是全透明的一张图。
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
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
    private static extern bool IsIconic(IntPtr hwnd);

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

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int x,
        int y,
        int width,
        int height,
        IntPtr hdcSrc,
        int srcX,
        int srcY,
        uint rop);
}
