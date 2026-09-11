using System;
using System.Threading;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 把窗口激活到前台。处理"前台锁"问题（SetForegroundWindow 限制）。
/// </summary>
public sealed class WindowActivator
{
    public void Activate(WindowInfo target)
    {
        if (target.Hwnd == IntPtr.Zero) return;

        // 1) 解锁：让目标进程能成为前台
        AllowSetForegroundWindow((uint)target.ProcessId);

        // 2) 若最小化，先恢复
        if (target.IsMinimized || IsIconic(target.Hwnd))
        {
            ShowWindow(target.Hwnd, SW_RESTORE);
        }
        else
        {
            ShowWindow(target.Hwnd, SW_SHOW);
        }

        // 3) 顶层化 + 置前
        BringWindowToTop(target.Hwnd);
        SetForegroundWindow(target.Hwnd);

        // 4) 失败重试 + 兜底（模拟 Alt 解锁）
        if (GetForegroundWindow() != target.Hwnd)
        {
            Logger.Warn($"首次 SetForegroundWindow 未生效，重试: {target.Title}");
            Thread.Sleep(50);
            BringWindowToTop(target.Hwnd);
            SetForegroundWindow(target.Hwnd);
        }

        if (GetForegroundWindow() != target.Hwnd)
        {
            // 兜底：模拟一次 Alt 键（按系统约定可以重置前台锁）
            SimulateAltPress();
            Thread.Sleep(50);
            SetForegroundWindow(target.Hwnd);
        }

        if (GetForegroundWindow() != target.Hwnd)
        {
            Logger.Error($"无法激活窗口: {target.Title} (HWND=0x{target.Hwnd:X})");
        }
    }

    private static void SimulateAltPress()
    {
        const byte VK_MENU = 0x12;
        keybd_event(VK_MENU, 0, 0, IntPtr.Zero);
        Thread.Sleep(20);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
    }

    /// <summary>
    /// 把**本进程自己的**窗口强行顶到前台并取得键盘焦点。
    ///
    /// 直接 SetForegroundWindow 经常无效：只有"拥有最后一次输入事件"的进程才有权设前台，
    /// 而低层键盘钩子路径下我们并没有这个权限（没有 WM_HOTKEY 附带的许可）。
    /// 办法是先 AttachThreadInput 把自己挂到当前前台线程的输入队列上——
    /// 这时系统认为我们和前台线程是"同一份输入"，SetForegroundWindow 就会被放行。
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var fg = GetForegroundWindow();
        if (fg == hwnd) return;

        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint myThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            ShowWindow(hwnd, SW_SHOW);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }

        if (GetForegroundWindow() != hwnd)
        {
            Logger.Warn($"ForceForeground 未生效 (HWND=0x{hwnd:X})");
        }
    }
}
