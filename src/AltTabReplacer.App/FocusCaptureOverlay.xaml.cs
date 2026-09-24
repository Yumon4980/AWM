using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;

namespace AltTabReplacer;

/// <summary>
/// 一次"捕获输入框"会话：<strong>每个显示器一个遮罩窗口</strong>。
///
/// 为什么不做一个横跨虚拟屏幕的大窗口：混合 DPI 下（主屏 125% + 副屏 200% 很常见），
/// 跨屏窗口的渲染位图按其中一个 DPI 生成，DWM 在另一块屏上裁切/缩放，会出现
/// "遮罩只盖住副屏一部分"的视觉问题（用户实测）。单屏单窗后每个 HWND 只属于
/// 一块显示器，没有跨屏缩放。
///
/// 点击流程（见 <see cref="FocusCaptureOverlay"/> 的类注释）：点击落在所在屏的遮罩上 →
/// 该遮罩同步 SW_HIDE → GetCursorPos + UIA FromPoint 取元素 → 成功则整个会话结束、
/// 所有遮罩关闭；失败则自己恢复显示并提示。
/// </summary>
public sealed class FocusCaptureSession
{
    private readonly List<FocusCaptureOverlay> _windows;
    private bool _finished;

    private FocusCaptureSession(List<FocusCaptureOverlay> windows)
    {
        _windows = windows;
        foreach (var w in windows) w._session = this;
    }

    /// <summary>捕获结束（成功或取消）。只触发一次；触发时所有遮罩窗口已关闭。</summary>
    public event Action<FocusTargetEntry?>? Finished;

    /// <summary>为每个显示器创建遮罩并显示。主屏实例拿键盘焦点（Esc 可用）。</summary>
    public static FocusCaptureSession Start(FocusTargetService service)
    {
        var windows = new List<FocusCaptureOverlay>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            // Screen.Bounds 在 PMv2 进程里就是物理像素
            windows.Add(new FocusCaptureOverlay(service, screen.Bounds));
        }

        var session = new FocusCaptureSession(windows);
        for (int i = 0; i < windows.Count; i++)
        {
            windows[i].ShowActivated = i == 0;
            windows[i].Show();
        }
        return session;
    }

    internal void Finish(FocusTargetEntry? entry)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(entry);
        foreach (var w in _windows) w.Close();
    }
}

/// <summary>
/// 单个显示器的捕获遮罩。点击被遮罩自己吃掉，不会误触目标程序。
///
/// 取点前必须把遮罩真正藏掉：UIA FromPoint 命中的是**最上层**窗口——也就是本遮罩。
/// <strong>坑</strong>：WPF 的 Visibility=Hidden 不会立即 SW_HIDE（推迟到 Render 优先级的
/// dispatcher 操作），紧接着的 FromPoint 会跑在"半隐藏"中间态——WPF provider 已不命中、
/// Win32 层还挂着，UIA 两头落空直接掉到桌面根窗口（csrss 的 #32769）。
/// 必须直接 P/Invoke ShowWindow(SW_HIDE) 同步隐藏，取完点再 SW_SHOWNA 恢复。
/// 坐标用 GetCursorPos 直接取物理像素（跨屏无换算问题，比 PointToScreen 稳）。
/// </summary>
public partial class FocusCaptureOverlay : Window
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;       // 显示但不激活：恢复遮罩时不抢焦点

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly FocusTargetService _service;
    private readonly System.Drawing.Rectangle _monitorRect;
    internal FocusCaptureSession? _session;   // 会话创建后回填（同文件内两个类协作）
    private bool _done;

    public FocusCaptureOverlay(FocusTargetService service, System.Drawing.Rectangle monitorRect)
    {
        InitializeComponent();
        _service = service;
        _monitorRect = monitorRect;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 用物理像素精确铺满本显示器。不走 WPF 的 Left/Top/Width/Height（DIP）：
        // 跨屏混合 DPI 时那套换算有歧义。
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero,
            _monitorRect.X, _monitorRect.Y, _monitorRect.Width, _monitorRect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_done || _session == null) return;

        // 光标位置就是点击位置，物理像素，跨屏无换算问题
        if (!GetCursorPos(out var pt))
        {
            Fail("无法获取光标位置，请重试（Esc 取消）");
            return;
        }

        // 同步隐藏遮罩再读点，FromPoint 才能穿透到目标程序（见类注释）
        var hwnd = new WindowInteropHelper(this).Handle;
        bool hidden = ShowWindow(hwnd, SW_HIDE);
        FocusTargetEntry? entry = null;
        try { entry = _service.CaptureAt(pt.X, pt.Y); }
        catch (Exception ex)
        {
            // 提权窗口（UIPI）会让 FromPoint 抛"拒绝访问"
            Logger.Warn($"捕获输入框失败: {ex.Message}");
        }
        finally
        {
            if (hidden) ShowWindow(hwnd, SW_SHOWNA);
        }

        // 桌面根窗口（csrss/#32769）= UIA 看不见点击点下的真窗口，典型原因是它以管理员
        // 运行。这种垃圾数据绝不能入库，否则"聚焦到桌面"每次切换都会白做一次。
        if (entry == null || FocusTargetService.IsDesktopRoot(entry))
        {
            Fail(DescribeUnreachable(pt));
            return;
        }
        if (string.Equals(entry.Process, FocusTargetService.OwnProcessName, StringComparison.OrdinalIgnoreCase))
        {
            Fail("这是 AltTabReplacer 自己的窗口，请点击其他程序的输入框");
            return;
        }

        _service.Save(entry);
        _done = true;
        _session.Finish(entry);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e) => Cancel();

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Cancel();
    }

    private void Cancel()
    {
        if (_done || _session == null) return;
        _done = true;
        _session.Finish(null);
    }

    private void Fail(string message)
    {
        HintText.Text = message;
        // 遮罩刚被 SW_SHOWNA 恢复（不激活），主动拿回键盘焦点让 Esc 依然可退
        Activate();
        Focus();
    }

    /// <summary>捕获落空时的诊断：看点击点下物理上是什么窗口，给出可行动的提示。</summary>
    private string DescribeUnreachable(Win32Point pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero || hwnd == GetDesktopWindow())
            return "这里没有识别到输入框，换个位置再点一次（Esc 取消）";

        var classSb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, classSb, 256);
        GetWindowThreadProcessId(hwnd, out var pid);

        string proc = "未知进程";
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            proc = p.ProcessName;
        }
        catch (Exception) { /* 进程恰好退出 */ }

        Logger.Info($"捕获落空: point=({pt.X},{pt.Y}) hwndClass={classSb} proc={proc}");
        return $"无法读取「{proc}」窗口的控件——它可能以管理员权限运行。\n请以管理员身份重新启动 AltTabReplacer 后再捕获该输入框。（Esc 取消）";
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Win32Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }
}
