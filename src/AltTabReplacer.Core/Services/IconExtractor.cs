using System;
using System.Text;
using AltTabReplacer.Core.Infrastructure;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 提取窗口图标。
///
/// 原来只用 `WM_GETICON`，但很多程序根本不响应这个消息（UWP、部分 Electron/Java 程序、
/// 自绘标题栏的应用），于是那些窗口就显示不出图标。这里改成逐级兜底：
///
///   1) `WM_GETICON`（大图标 → 小图标）—— 应用自己声明的，质量最好
///   2) `GetClassLongPtr(GCLP_HICON / GCLP_HICONSM)` —— 注册窗口类时挂的图标
///   3) `ExtractIconEx` 直接扒 exe 的图标资源 —— 对不响应消息的程序最有效
///   4) `SHGetFileInfo` —— 走 shell 的图标解析，连没有图标资源的程序也能拿到默认图标
///
/// 返回的 HICON **一律是调用方拥有的副本**（借来的用 CopyIcon 复制），
/// 所以调用方用完后统一 DestroyIcon 即可，不用关心来源。
/// </summary>
public static class IconExtractor
{
    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取窗口图标。返回 IntPtr.Zero 表示四级兜底全都失败。</summary>
    public static IntPtr GetWindowIcon(IntPtr hwnd, int processId)
    {
        // 1) WM_GETICON
        var icon = CopyOwned(SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_BIG, IntPtr.Zero))
                ?? CopyOwned(SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL, IntPtr.Zero))
                ?? CopyOwned(SendMessage(hwnd, WM_GETICON, (IntPtr)ICON_SMALL2, IntPtr.Zero));
        if (icon.HasValue) return icon.Value;

        // 2) 窗口类的图标
        var cls = CopyOwned(GetClassLongPtr(hwnd, GCLP_HICON))
               ?? CopyOwned(GetClassLongPtr(hwnd, GCLP_HICONSM));
        if (cls.HasValue) return cls.Value;

        var exe = GetExecutablePath(processId);
        if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;

        // 3) 直接扒 exe 的图标资源
        try
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            uint n = ExtractIconEx(exe, 0, large, small, 1);
            if (n > 0)
            {
                if (large[0] != IntPtr.Zero)
                {
                    if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
                    return large[0];
                }
                if (small[0] != IntPtr.Zero) return small[0];
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"ExtractIconEx 失败 ({exe}): {ex.Message}");
        }

        // 4) shell 兜底
        try
        {
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(exe, 0, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON);
            if (r != IntPtr.Zero && info.hIcon != IntPtr.Zero) return info.hIcon;
        }
        catch (Exception ex)
        {
            Logger.Warn($"SHGetFileInfo 失败 ({exe}): {ex.Message}");
        }

        return IntPtr.Zero;
    }

    /// <summary>借来的图标句柄不是我们的，复制一份再返回，保证所有权统一。</summary>
    private static IntPtr? CopyOwned(IntPtr borrowed)
    {
        if (borrowed == IntPtr.Zero) return null;
        var copy = CopyIcon(borrowed);
        return copy == IntPtr.Zero ? null : copy;
    }

    /// <summary>释放 <see cref="GetWindowIcon"/> 返回的句柄。必须与它配对调用。</summary>
    public static void Release(IntPtr hIcon)
    {
        if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
    }

    /// <summary>进程 exe 的完整路径，用于第 3、4 级兜底。</summary>
    public static string? GetExecutablePath(int pid)
    {
        if (pid <= 0) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
        }
        catch (Exception ex)
        {
            Logger.Warn($"取进程路径失败 (pid={pid}): {ex.Message}");
        }
        finally
        {
            CloseHandle(h);
        }
        return null;
    }
}
