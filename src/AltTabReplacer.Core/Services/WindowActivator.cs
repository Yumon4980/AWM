using System;
using System.Diagnostics;
using System.Threading;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 把窗口激活到前台。处理"前台锁"问题（SetForegroundWindow 限制）。
/// 程序组合还用它来"未开则启动"。
/// </summary>
public sealed class WindowActivator
{
    /// <summary>
    /// 打开一个程序组合：按成员顺序，已开则激活、未开则用 exe 启动。
    /// 同进程只激活/启动一次（Members 可能多 ref 对应同一进程）。
    /// </summary>
    public void OpenCombination(ResolvedSlot combo)
    {
        var refs = combo.Members;
        if (refs is not { Count: > 0 }) return;

        var matchedProcs = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var activatedHwnds = new System.Collections.Generic.HashSet<IntPtr>();
        foreach (var w in combo.Windows)
        {
            matchedProcs.Add(w.ProcessName);
            if (!activatedHwnds.Add(w.Hwnd)) continue;
            try { Activate(w); }
            catch (Exception ex) { Logger.Error($"激活失败: {ex.Message}"); }
        }

        // 未开的启动：同进程只启动一次（Members 可能多 ref 对应同一进程）
        var launchedProcs = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var spec in refs)
        {
            if (matchedProcs.Contains(spec.Process)) continue;
            if (!launchedProcs.Add(spec.Process)) continue;
            if (!string.IsNullOrEmpty(spec.ExePath)) Launch(spec.ExePath!);
            else Logger.Warn($"程序组合成员 {spec.Process} 没有可执行路径，无法启动");
        }
    }

    /// <summary>用 ShellExecute 启动一个可执行文件（走默认关联 / 工作目录）。</summary>
    public static void Launch(string exePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
            });
            Logger.Info($"启动程序: {exePath}");
        }
        catch (Exception ex)
        {
            Logger.Error($"启动失败 {exePath}: {ex.Message}");
        }
    }

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

    /// <summary>
    /// 让窗口变成"点击穿透 + 不抢激活"。
    ///
    /// 拖动时跟随光标的 ghost 窗口是 Topmost 且正好压在光标底下，
    /// 不打这个标记的话它会抢走鼠标消息，SelectorWindow 就收不到 MouseMove，
    /// 落点判定会一直停在拖动开始那一刻——表现为"拖了但没反应"。
    ///
    /// 注意：WPF 的 <c>IsHitTestVisible</c> 只管窗口**内部**的命中测试，
    /// 管不了 Win32 层面谁接收鼠标消息，所以必须设置 WS_EX_TRANSPARENT。
    /// </summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        long ex = GetWindowLong(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE));
    }
}
