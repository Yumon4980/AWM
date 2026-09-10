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
}
