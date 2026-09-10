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
/// 截取窗口截图（PrintWindow + PW_RENDERFULLCONTENT）。
/// 能正确处理 DirectX / 现代应用（含 UWP）。
/// 必须在 UI 线程调用。
/// </summary>
public sealed class WindowCaptureService
{
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>
    /// 截取窗口并缩放到目标尺寸。
    /// </summary>
    public BitmapSource? Capture(IntPtr hwnd, int targetWidth, int targetHeight)
    {
        if (hwnd == IntPtr.Zero) return null;

        if (!GetWindowRect(hwnd, out RECT rect)) return null;
        int w = rect.Width;
        int h = rect.Height;
        if (w <= 0 || h <= 0) return null;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            // Bitmap -> BitmapSource（按目标尺寸缩放）
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(targetWidth, targetHeight));
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
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);
}
