using System;
using System.Collections.Generic;
using System.Text;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 枚举当前可见顶层窗口。
/// 输出已按 z-order 倒序（最近激活的在前），并通过 <see cref="IRuleEngine"/> 二次排序。
/// </summary>
public sealed class WindowEnumerator
{
    private readonly IRuleEngine _ruleEngine;
    private readonly IntPtr _ownHwnd;

    public WindowEnumerator(IRuleEngine ruleEngine, IntPtr ownHwnd)
    {
        _ruleEngine = ruleEngine;
        _ownHwnd = ownHwnd;
    }

    public IReadOnlyList<WindowInfo> Enumerate()
    {
        var collected = new List<WindowInfo>();

        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (hWnd == _ownHwnd) return true;

                // 1) 可见
                if (!IsWindowVisible(hWnd)) return true;

                // 2) 非工具窗口
                var exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((exStyle.ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;

                // 3) 非 Cloaked（UWP / Store 后台应用）
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                // 4) 标题非空
                int len = GetWindowTextLength(hWnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                // 5) PID + 进程名
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;
                var procName = GetProcessFileName((int)pid);
                if (string.IsNullOrEmpty(procName)) return true;

                // 6) 最小化状态
                bool isMin = IsIconic(hWnd);

                collected.Add(new WindowInfo(hWnd, title, procName, (int)pid, isMin));
            }
            catch (Exception ex)
            {
                Logger.Warn($"枚举 {hWnd:X} 失败: {ex.Message}");
            }
            return true;
        }, IntPtr.Zero);

        // z-order 倒序：EnumWindows 自身按 z-order 输出，reverse 即"最近激活的在前"
        collected.Reverse();

        // 应用规则（置顶 / 排除 / 优先级）
        return _ruleEngine.Apply(collected);
    }

    private static string? GetProcessFileName(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size))
            {
                return System.IO.Path.GetFileName(sb.ToString());
            }
        }
        finally
        {
            CloseHandle(h);
        }
        return null;
    }
}
