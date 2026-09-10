using System;
using System.Runtime.InteropServices;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 全局低层键盘钩子（WH_KEYBOARD_LL）。
///
/// 用途：拦截选择器显示期间的 123/QWE 等按键，绕开 IME。
/// hook 回调在系统线程上执行，所以：
///   - 必须**快速返回**（&lt; 1ms），不能做任何 WPF 调用
///   - 只能做"是否拦截"的判断 + 简单入队
///   - WPF 端的处理通过 <see cref="KeyObserved"/> 事件在 WPF 线程上消费
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    private readonly HookProc _proc;        // 必须保持引用，否则被 GC
    private IntPtr _hookId = IntPtr.Zero;
    private Func<int, bool>? _shouldIntercept;

    /// <summary>WPF 线程订阅此事件来接收被拦截的按键。</summary>
    public event Action<int>? KeyObserved;

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install(Func<int, bool> shouldIntercept)
    {
        if (_hookId != IntPtr.Zero) return;
        _shouldIntercept = shouldIntercept;
        // 模块句柄 = 当前进程主模块（.NET 8 简化处理可用 GetModuleHandle(null)）
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hookId == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            AltTabReplacer.Core.Infrastructure.Logger.Error($"SetWindowsHookEx 失败: err={err}");
        }
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _shouldIntercept = null;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool intercept = _shouldIntercept?.Invoke(info.vkCode) ?? false;
            if (intercept)
            {
                KeyObserved?.Invoke(info.vkCode);
                return (IntPtr)1;  // 拦截，不再传递给后续
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ---- P/Invoke ----

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
