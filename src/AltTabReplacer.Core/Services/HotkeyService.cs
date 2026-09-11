using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using AltTabReplacer.Core.Infrastructure;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 全局热键服务。
///
/// 行为契约：
///   1) Register(hwnd, mods, key)：在指定 HWND 上注册热键，失败返回 false（被系统占用）
///   2) 修饰键按下后，按下时触发 <see cref="HotkeyPressed"/>；修饰键全部松开时触发 <see cref="HotkeyReleased"/>
///   3) 修饰键轮询用 DispatcherTimer（UI 线程，~30Hz），不阻塞消息循环
///
/// 两条实现路径：
///   - 普通组合：RegisterHotKey + 窗口子类化拦 WM_HOTKEY
///   - 系统保留组合（Alt+Tab / Alt+Esc）：RegisterHotKey 注册不到，改由调用方的
///     <see cref="LowLevelKeyboardHook"/> 把按键吞掉后回调 <see cref="TryHandleHookKey"/>。
///     吞掉这一步就是"替换系统 Alt+Tab"的关键——系统任务切换器收不到按键就不会弹出。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HOTKEY_ID = 0xBEEF;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;     // Left Alt
    private const int VK_RMENU = 0xA5;     // Right Alt
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private readonly Dispatcher _dispatcher;
    private IntPtr _hwnd;
    private SubclassProc? _subclassProc;   // 保持引用，避免被 GC
    private DispatcherTimer? _releaseTimer;
    // 下面两个标志会被 hook 线程和 UI 线程（释放轮询）同时读写，标 volatile 防止被缓存进寄存器
    private volatile bool _pressed;
    private bool _lastStillDown = true;     // 上次轮询的 modifier 状态
    private uint _registeredMods;
    private uint _registeredVk;
    private bool _hookMode;
    private volatile bool _hookKeyHeld;     // 钩子路径下用于过滤自动重复（等价 MOD_NOREPEAT）

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;

    /// <summary>true 表示当前热键走低层键盘钩子路径，需要调用方转发 <see cref="TryHandleHookKey"/>。</summary>
    public bool IsHookMode => _hookMode;

    public HotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool Register(IntPtr hwnd, string modifiers, string key)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("hwnd is zero");
        Unregister();

        _hwnd = hwnd;
        _registeredMods = ParseModifiers(modifiers);
        _registeredVk = (uint)ParseKey(key);
        _hookMode = IsSystemReserved(_registeredMods, _registeredVk);

        if (_hookMode)
        {
            // Alt+Tab 属于系统保留组合：RegisterHotKey 会失败，就算成功也收不到 WM_HOTKEY。
            // 由 LowLevelKeyboardHook 在更早的位置截获（见 TryHandleHookKey）。
            Logger.Info($"热键 mods=0x{_registeredMods:X} vk=0x{_registeredVk:X} 是系统保留组合，改用低层键盘钩子接管");
            StartReleaseTimer();
            return true;
        }

        // 装上子类化钩子（拦截 WM_HOTKEY）
        _subclassProc = SubclassCallback;
        if (!SetWindowSubclass(hwnd, _subclassProc, (IntPtr)HOTKEY_ID, IntPtr.Zero))
        {
            Logger.Error($"SetWindowSubclass 失败: {GetLastError()}");
            return false;
        }

        // 注册热键本身
        if (!RegisterHotKey(hwnd, HOTKEY_ID, _registeredMods, _registeredVk))
        {
            int err = GetLastError();
            Logger.Error($"RegisterHotKey 失败 (错误码 {err})：通常意味着热键已被其他程序占用");
            RemoveWindowSubclass(hwnd, _subclassProc, (IntPtr)HOTKEY_ID);
            _subclassProc = null;
            return false;
        }

        StartReleaseTimer();

        Logger.Info($"已注册热键: mods=0x{_registeredMods:X} vk=0x{_registeredVk:X}");
        return true;
    }

    /// <summary>启动修饰键轮询（用于检测"松开"事件）。</summary>
    private void StartReleaseTimer()
    {
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _releaseTimer.Tick += OnReleaseTick;
        _releaseTimer.Start();
    }

    public void Unregister()
    {
        if (_hwnd == IntPtr.Zero) return;

        _releaseTimer?.Stop();
        _releaseTimer = null;

        UnregisterHotKey(_hwnd, HOTKEY_ID);
        if (_subclassProc != null)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, (IntPtr)HOTKEY_ID);
        }
        _subclassProc = null;
        _pressed = false;
        _hookMode = false;
        _hookKeyHeld = false;
        Logger.Info("已注销热键");
    }

    public void Dispose() => Unregister();

    // ----------------------------------------------------------
    //  低层键盘钩子路径（Alt+Tab）
    // ----------------------------------------------------------

    /// <summary>
    /// 由全局低层键盘钩子在 **hook 线程** 上调用，必须立即返回。
    /// 返回 true = 这个按键属于本热键、已被吞掉，不要再传给系统（系统 Alt+Tab 因此不会弹出）。
    ///
    /// 注意这里**只负责通知**，不碰前台窗口。
    /// 抢前台必须由订阅方在知道"这次是开还是关"之后自己做：
    /// 在这里抢的话，已经打开的选择器会因为失焦被自动关掉，等 HotkeyPressed 真正跑到时
    /// 它看到的已经是"没开"，于是又新开一个——一次按键变成先关后开。
    /// </summary>
    public bool TryHandleHookKey(in LowLevelKeyboardHook.KeyEvent e)
    {
        if (!_hookMode || e.Vk != _registeredVk) return false;

        // 抬键必须**无条件**先处理，绝不能放在 ModifiersHeld 后面。
        // 原因：用户经常先松 Alt 再松 Tab，那一下 Tab 的 keyup 里 Alt 已经是 false，
        // 若先判修饰键就会直接 return false，_hookKeyHeld 永远停在 true——
        // 下一次 Alt+Tab 会命中"自动重复"分支被静默吞掉，表现就是热键时灵时不灵。
        if (!e.IsDown)
        {
            bool wasOurs = _hookKeyHeld;
            _hookKeyHeld = false;
            return wasOurs;         // 只吞掉我们自己按下的那次所配对的 keyup
        }

        if (!ModifiersHeld(e))
        {
            // 这次按下没被我们吞，配对的抬起也不该吞，否则前台程序会收到一个没有 keyup 的
            // Tab（卡键）。顺手清掉可能残留的旧状态——每次普通 Tab 都是一次自我校正。
            _hookKeyHeld = false;
            return false;
        }

        if (_hookKeyHeld) return true;   // 按住不放的自动重复：吞掉但不重复触发

        _hookKeyHeld = true;
        _pressed = true;
        _dispatcher.BeginInvoke(() =>
        {
            Logger.Info("热键触发");
            HotkeyPressed?.Invoke();
        });
        return true;
    }

    /// <summary>配置的修饰键是否都按着（允许多按，Alt+Shift+Tab / Ctrl+Alt+Tab 也一并接管）。</summary>
    private bool ModifiersHeld(in LowLevelKeyboardHook.KeyEvent e)
    {
        if ((_registeredMods & MOD_ALT) != 0 && !e.Alt) return false;
        if ((_registeredMods & MOD_CONTROL) != 0 && !e.Ctrl) return false;
        if ((_registeredMods & MOD_SHIFT) != 0 && !e.Shift) return false;
        if ((_registeredMods & MOD_WIN) != 0 && !e.Win) return false;
        return true;
    }

    /// <summary>系统保留、RegisterHotKey 拿不到的组合。</summary>
    private static bool IsSystemReserved(uint mods, uint vk)
    {
        uint m = mods & ~MOD_NOREPEAT;
        return (m & MOD_ALT) != 0 && (vk == (uint)VK_TAB || vk == (uint)VK_ESCAPE);
    }

    // ----------------------------------------------------------
    //  WM_HOTKEY 回调
    // ----------------------------------------------------------

    private IntPtr SubclassCallback(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            _pressed = true;
            // 关键：WM_HOTKEY 给了本进程"设前台"的窗口。立刻把自己的 HostWindow 拉到前台，
            // 这样后续 SelectorWindow.Show() 才能真正获得焦点（否则按键会被原前台窗口截走）。
            SetForegroundWindow(hWnd);
            HotkeyPressed?.Invoke();
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void OnReleaseTick(object? sender, EventArgs e)
    {
        if (!_pressed) return;

        // 检查"所有修饰键都已松开"
        bool stillDown = false;
        if ((_registeredMods & MOD_CONTROL) != 0 && AnyKeyDown(VK_LCONTROL, VK_RCONTROL)) stillDown = true;
        if ((_registeredMods & MOD_ALT)     != 0 && AnyKeyDown(VK_LMENU, VK_RMENU))     stillDown = true;
        if ((_registeredMods & MOD_SHIFT)   != 0 && AnyKeyDown(VK_LSHIFT, VK_RSHIFT))   stillDown = true;
        if ((_registeredMods & MOD_WIN)     != 0 && AnyKeyDown(VK_LWIN, VK_RWIN))       stillDown = true;

        if (stillDown != _lastStillDown)
        {
            _lastStillDown = stillDown;
        }

        if (!stillDown)
        {
            _pressed = false;
            // 双保险：修饰键全松了，热键键位不可能还"按着"。
            // 万一有哪条路径漏掉了 keyup（比如钩子重挂的瞬间正好丢了一个事件），在这里兜回来，
            // 避免 _hookKeyHeld 卡住导致下一次热键被静默吞掉。
            _hookKeyHeld = false;
            HotkeyReleased?.Invoke();
        }
    }

    private static bool AnyKeyDown(params int[] vks)
    {
        foreach (var vk in vks)
        {
            // 高位（bit 15）表示当前是否按下
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        return false;
    }

    // ----------------------------------------------------------
    //  解析
    // ----------------------------------------------------------

    private static uint ParseModifiers(string csv)
    {
        uint result = MOD_NOREPEAT;
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    result |= MOD_CONTROL; break;
                case "alt":
                    result |= MOD_ALT; break;
                case "shift":
                    result |= MOD_SHIFT; break;
                case "win":
                case "meta":
                    result |= MOD_WIN; break;
                default:
                    Logger.Warn($"未知修饰键: {raw}");
                    break;
            }
        }
        return result;
    }

    private static int ParseKey(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "caps" or "capslock" => 0x14,
            "backspace" or "back" => 0x08,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            _ when name.Length == 1 && char.IsDigit(name[0]) => name[0],
            _ when name.Length == 1 && char.IsLetter(name[0]) => char.ToUpper(name[0]),
            _ => throw new ArgumentException($"无法解析按键: {name}")
        };
    }

    private static int GetLastError() => Marshal.GetLastWin32Error();
}
