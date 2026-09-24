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
    private readonly FocusTargetService? _focusTargets;

    public WindowActivator(FocusTargetService? focusTargets = null)
    {
        _focusTargets = focusTargets;
    }

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
        WindowInfo? lastActivated = null;
        foreach (var w in combo.Windows)
        {
            matchedProcs.Add(w.ProcessName);
            if (!activatedHwnds.Add(w.Hwnd)) continue;
            try
            {
                // 输入框聚焦只做在**最后激活**的那个窗口上：中间成员做了也会被后面的
                // 激活顶掉，而对非前台窗口 SetFocus 反而会把它顶回前台、打乱组合次序
                if (ActivateCore(w, applyFocus: false)) lastActivated = w;
            }
            catch (Exception ex) { Logger.Error($"激活失败: {ex.Message}"); }
        }
        if (lastActivated != null) _focusTargets?.BeginApply(lastActivated);

        // 未开的启动：同进程只启动一次（Members 可能多 ref 对应同一进程）
        var launchedProcs = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var spec in refs)
        {
            if (matchedProcs.Contains(spec.Process)) continue;
            if (!launchedProcs.Add(spec.Process)) continue;
            if (!string.IsNullOrEmpty(spec.ExePath)) Launch(spec.ExePath!, spec.LaunchArgs);
            else Logger.Warn($"程序组合成员 {spec.Process} 没有可执行路径，无法启动");
        }

        // 全体成员都没开、又一条可执行路径都没启动时，退回整槽位的兜底路径
        if (combo.Windows.Count == 0 && launchedProcs.Count == 0 && !string.IsNullOrEmpty(combo.LaunchPath))
            Launch(combo.LaunchPath, combo.LaunchArgs);

        // explorer 路径窗口特别提示：标题被 Windows 截断（长路径带 "..."）时
        // 程序组合 / 槽位的 LaunchArgs 都为 null，explorer 启动后会开"主页"。
        // 需要在右键菜单里手动设置启动参数。
        if (launchedProcs.Count > 0 && combo.LaunchPath == null
            && combo.Members?.Any(m => string.Equals(m.Process, "explorer", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(m.LaunchArgs)) == true)
        {
            Logger.Info("explorer 成员没有记录精确路径（长路径会被 Windows 截断），可能启动为\"主页\"。右键槽位可手动设置启动参数。");
        }
    }

    /// <summary>
    /// 重新启动一个"窗口已全部关闭"的锁定槽位：按成员记录的可执行路径逐个启动
    /// （同进程只启动一次）。成员路径全都缺失时退回整槽位的兜底路径
    /// <see cref="ResolvedSlot.LaunchPath"/>。
    /// </summary>
    public void RelaunchClosed(ResolvedSlot slot)
    {
        var launched = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (slot.Members is { Count: > 0 })
        {
            foreach (var spec in slot.Members)
            {
                if (string.IsNullOrEmpty(spec.ExePath)) continue;
                if (!launched.Add(spec.Process)) continue;
                Launch(spec.ExePath!, spec.LaunchArgs);
            }
        }

        if (launched.Count == 0 && !string.IsNullOrEmpty(slot.LaunchPath))
            Launch(slot.LaunchPath, slot.LaunchArgs);

        if (launched.Count == 0 && string.IsNullOrEmpty(slot.LaunchPath))
            Logger.Warn($"重新启动失败: {slot.Name} 没有记录可执行路径（解锁再重新锁定可补记）");
    }

    /// <summary>用 ShellExecute 启动一个可执行文件（走默认关联 / 工作目录）。</summary>
    public static void Launch(string exePath, string? args = null)
    {
        try
        {
            // 含空格的路径或 args 自动加引号；简单替换成 Arguments 字段更安全
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args ?? "",
                UseShellExecute = true,
            });
            Logger.Info($"启动程序: {exePath}{(string.IsNullOrEmpty(args) ? "" : " " + args)}");
        }
        catch (Exception ex)
        {
            Logger.Error($"启动失败 {exePath} {args}: {ex.Message}");
        }
    }

    /// <summary>激活窗口；成功成为前台后按配置聚焦录制的输入框。</summary>
    public void Activate(WindowInfo target) => ActivateCore(target, applyFocus: true);

    /// <summary>激活并返回是否真的成了前台窗口（决定输入框聚焦做不做）。</summary>
    private bool ActivateCore(WindowInfo target, bool applyFocus)
    {
        if (target.Hwnd == IntPtr.Zero) return false;

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
            return false;
        }

        Logger.Info($"已切换: {target.ProcessName}（{target.Title}）");

        // 激活成功后再聚焦输入框：切窗动作本身不能被 UIA 树查找（几十到几百毫秒）拖慢，
        // BeginApply 内部异步执行。聚焦失败只记日志，不影响切换本身。
        if (applyFocus) _focusTargets?.BeginApply(target);
        return true;
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
