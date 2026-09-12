using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AltTabReplacer.Core.Infrastructure;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;
using static AltTabReplacer.Core.Infrastructure.Win32.Structures;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 截取窗口截图（PrintWindow + PW_RENDERFULLCONTENT，失败再用 BitBlt 兜底）。
/// 能正确处理 DirectX / 现代应用（含 UWP、Electron）。
/// 必须在 UI 线程调用。
/// </summary>
public sealed class WindowCaptureService
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>
    /// 截取窗口**客户区**并缩放到目标尺寸。
    ///
    /// 只截客户区（不含标题栏/边框），比例就匹配窗口内容的实际形状，
    /// 预览里看着不会"上下黑边一大截"。原理：把 DC 视口原点平移到 (-windowLeft, -windowTop)，
    /// 再用客户区尺寸的 bitmap 接住，标题栏就画到负坐标、超出 bitmap 被裁掉。
    /// </summary>
    public BitmapSource? Capture(IntPtr hwnd, int targetWidth, int targetHeight)
    {
        if (hwnd == IntPtr.Zero) return null;

        if (!GetWindowRect(hwnd, out RECT wRect)) return null;
        if (!GetClientRect(hwnd, out RECT cRect)) return null;
        int w = cRect.Width;
        int h = cRect.Height;
        if (w <= 0 || h <= 0) return null;

        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    // 把视口原点设到窗口外框的左上角之"负值"，客户区就落在 (0,0)
                    SetViewportOrgEx(hdc, -wRect.Left, -wRect.Top, IntPtr.Zero);
                    PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            // PrintWindow 对部分 DWM 合成窗口（Electron / 硬件加速）返回全黑——窗口把内容渲染到 GPU surface，
            // PrintWindow 拿不到。兜底：BitBlt + CAPTUREBLT 直接拷窗口客户区 DC，拿到 DWM 合成后的真画面。
            if (IsMostlyBlack(bmp))
            {
                var fallback = CaptureWithBitBlt(hwnd, w, h);
                if (fallback != null)
                {
                    bmp.Dispose();
                    bmp = fallback;
                }
            }

            // Bitmap -> BitmapSource（按目标尺寸缩放）
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                // targetWidth/Height=0,0 表示"用原图大小"，必须用 FromEmptyOptions（FromWidthAndHeight 不接受 0）
                var bmpOptions = (targetWidth > 0 && targetHeight > 0)
                    ? BitmapSizeOptions.FromWidthAndHeight(targetWidth, targetHeight)
                    : BitmapSizeOptions.FromEmptyOptions();
                var bmpSrc = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, bmpOptions);
                bmpSrc.Freeze();   // 跨线程 + XAML 绑定安全
                return bmpSrc;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"截取窗口 0x{hwnd:X} 失败: {ex.Message}");
            return null;
        }
        finally
        {
            bmp?.Dispose();
        }
    }

    /// <summary>
    /// 用 BitBlt 从窗口客户区 DC 拷一份。GetDC(hwnd) 的 DC 原点在客户区左上角，
    /// 目标 bitmap 也是客户区尺寸、(0,0) 为左上角，所以两边都从 (0,0) 起拷就对齐。
    /// CAPTUREBLT 让分层/半透明像素也能拷下来。
    /// </summary>
    private static Bitmap? CaptureWithBitBlt(IntPtr hwnd, int w, int h)
    {
        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            IntPtr hdc = g.GetHdc();
            try
            {
                IntPtr hdcSrc = GetDC(hwnd);
                if (hdcSrc == IntPtr.Zero)
                {
                    bmp.Dispose();
                    return null;
                }
                try
                {
                    BitBlt(hdc, 0, 0, w, h, hdcSrc, 0, 0, SRCCOPY | CAPTUREBLT);
                }
                finally
                {
                    ReleaseDC(hwnd, hdcSrc);
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }

            // 兜底也黑了（窗口被遮住/最小化）就放弃
            if (IsMostlyBlack(bmp))
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }
        catch
        {
            bmp?.Dispose();
            return null;
        }
    }

    /// <summary>稀疏采样若干像素点，99% 都是接近纯黑就视为"黑图"。</summary>
    private static bool IsMostlyBlack(Bitmap bmp)
    {
        int step = Math.Max(1, Math.Min(bmp.Width, bmp.Height) / 16);
        int samples = 0;
        int black = 0;
        for (int y = 0; y < bmp.Height; y += step)
        {
            for (int x = 0; x < bmp.Width; x += step)
            {
                var c = bmp.GetPixel(x, y);
                samples++;
                if (c.R < 8 && c.G < 8 && c.B < 8) black++;
            }
        }
        return samples > 0 && (double)black / samples >= 0.99;
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
