using System;
using System.Runtime.InteropServices;
using System.Threading;
using AltTabReplacer.Core.Infrastructure;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 全局低层键盘钩子（WH_KEYBOARD_LL）。
///
/// 两个用途：
///   1) 接管 Alt+Tab —— 系统保留组合，RegisterHotKey 注册不到，只能在这里把它吞掉
///   2) 拦截选择器显示期间的 123/QWE 等按键，绕开 IME
///
/// **钩子跑在自己的专用线程上，不能挂在 WPF UI 线程上。**
/// MSDN 明确：WH_KEYBOARD_LL 的回调是"投递消息到装钩子的那个线程"来执行的，
/// 所以那个线程必须一直在 pump 消息。挂在 UI 线程上时，只要 UI 线程在忙
/// （本程序最典型的就是打开选择器时对 20+ 个窗口逐个 PrintWindow 截图，轻松几百毫秒），
/// 回调就没法被派发；超过 LowLevelHooksTimeout（默认 300ms）系统会直接跳过甚至摘掉钩子，
/// 表现就是"热键偶尔没反应"。专用线程常年空转，不会被 UI 的活儿拖住。
///
/// 回调仍然必须**快速返回**：不要在里面写日志（Logger 是同步文件 IO）、不要碰 WPF，
/// 只做判断 + 投递。
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;   // Alt 按住时的按键走这条（Alt+Tab 就在这里）
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_QUIT = 0x0012;
    private const int WM_REHOOK = 0x0400 + 1;   // WM_APP+1：自定义线程消息，通知钩子线程重挂
    private const int LLKHF_ALTDOWN = 0x20;

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    /// <summary>钩子回调对一次按键的处置。</summary>
    public enum HookAction
    {
        /// <summary>放行，交给系统和前台应用。</summary>
        Pass,
        /// <summary>吞掉，但不通知 WPF 线程（调用方已自行投递）。</summary>
        Swallow,
        /// <summary>吞掉，并通过 <see cref="KeyObserved"/> 通知 WPF 线程。</summary>
        SwallowAndObserve,
    }

    /// <summary>一次按键事件的快照（在 hook 线程上构造，不含任何托管资源）。</summary>
    public readonly record struct KeyEvent(int Vk, bool IsDown, bool Alt, bool Ctrl, bool Shift, bool Win);

    private readonly HookProc _proc;        // 必须保持引用，否则被 GC
    private IntPtr _hookId = IntPtr.Zero;
    private Func<KeyEvent, HookAction>? _decide;
    private Thread? _thread;
    private uint _threadId;
    private int _installError;
    private int _callbackErrorLogged;

    /// <summary>WPF 线程订阅此事件来接收被拦截的按键。注意事件在 hook 线程上触发。</summary>
    public event Action<KeyEvent>? KeyObserved;

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    public bool IsInstalled => Volatile.Read(ref _hookId) != IntPtr.Zero;

    /// <summary>最近一次 SetWindowsHookEx 失败的 Win32 错误码（0 表示没失败过）。</summary>
    public int LastInstallError => Volatile.Read(ref _installError);

    /// <summary>装钩子。会起一个专用线程并在上面跑消息循环，返回 false 表示装失败。</summary>
    public bool Install(Func<KeyEvent, HookAction> decide)
    {
        if (_thread != null) return IsInstalled;
        _decide = decide;

        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => HookThreadMain(ready))
        {
            IsBackground = true,
            Name = "AltTabReplacer.KeyboardHook",
            Priority = ThreadPriority.Highest,   // 回调有 300ms 硬预算，别让它排队
        };
        _thread.Start();
        ready.Wait(5000);
        return IsInstalled;
    }

    private void HookThreadMain(ManualResetEventSlim ready)
    {
        var id = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        Volatile.Write(ref _hookId, id);
        Volatile.Write(ref _installError, id == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0);
        _threadId = GetCurrentThreadId();
        ready.Set();

        if (id == IntPtr.Zero) return;

        // 消息循环：LL 钩子的回调就是靠这个循环被派发的，少了它钩子形同虚设
        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REHOOK)
            {
                DoRehook();
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        var final = Volatile.Read(ref _hookId);
        if (final != IntPtr.Zero) UnhookWindowsHookEx(final);
        Volatile.Write(ref _hookId, IntPtr.Zero);
    }

    /// <summary>只能在钩子线程上调用：摘钩 + 重挂。</summary>
    private void DoRehook()
    {
        var old = Volatile.Read(ref _hookId);
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);

        var id = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        Volatile.Write(ref _hookId, id);
        Volatile.Write(ref _installError, id == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0);
    }

    /// <summary>
    /// 请求钩子线程摘钩后重新挂一次。两个作用：
    ///   1) **抢回优先级** —— 系统按"后装的先调用"派发钩子链，别的程序后装的钩子会排在我们前面，
    ///      重挂一次就回到链头，Alt+Tab 先经过我们
    ///   2) **自愈** —— 万一被系统静默摘掉，重挂能恢复
    /// 异步：消息投给钩子线程，不阻塞调用方。
    /// </summary>
    public bool Reinstall()
    {
        if (_threadId == 0) return false;
        return PostThreadMessage(_threadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
    }

    public void Uninstall()
    {
        var t = _thread;
        if (t == null) return;
        _thread = null;

        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(2000);
        _threadId = 0;
        _decide = null;
    }

    public void Dispose() => Uninstall();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            // 这里的异常绝对不能逃逸：回调是操作系统直接调进来的，
            // 异常穿过托管/原生边界会直接把进程干掉（表现为"热键按一下就闪退"）。
            try
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown || isUp)
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    var e = new KeyEvent(
                        info.vkCode,
                        isDown,
                        // LLKHF_ALTDOWN 只在 SYSKEY 消息上可靠，再用 GetAsyncKeyState 兜一层
                        (info.flags & LLKHF_ALTDOWN) != 0 || IsDown(VK_MENU),
                        IsDown(VK_CONTROL),
                        IsDown(VK_SHIFT),
                        IsDown(VK_LWIN) || IsDown(VK_RWIN));

                    switch (_decide?.Invoke(e) ?? HookAction.Pass)
                    {
                        case HookAction.SwallowAndObserve:
                            KeyObserved?.Invoke(e);
                            return (IntPtr)1;   // 拦截，不再传递给后续
                        case HookAction.Swallow:
                            return (IntPtr)1;
                    }
                }
            }
            catch (Exception ex)
            {
                // 只记第一次。Logger 是同步文件 IO，如果这是个每次按键都触发的 bug，
                // 每击一键都写盘会撑爆 LowLevelHooksTimeout 把钩子搞掉，反而更糟。
                if (Interlocked.Exchange(ref _callbackErrorLogged, 1) == 0)
                {
                    Logger.Error("键盘钩子回调异常（已放行该按键；后续同类异常不再重复记录）", ex);
                }
            }
        }
        return CallNextHookEx(Volatile.Read(ref _hookId), nCode, wParam, lParam);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
