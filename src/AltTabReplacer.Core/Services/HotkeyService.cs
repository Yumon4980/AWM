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
    private bool _pressed;
    private bool _lastStillDown = true;     // 上次轮询的 modifier 状态
    private uint _registeredMods;
    private uint _registeredVk;

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;

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

        // 启动修饰键轮询（用于检测"松开"事件）
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _releaseTimer.Tick += OnReleaseTick;
        _releaseTimer.Start();

        Logger.Info($"已注册热键: mods=0x{_registeredMods:X} vk=0x{_registeredVk:X}");
        return true;
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
        Logger.Info("已注销热键");
    }

    public void Dispose() => Unregister();

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
