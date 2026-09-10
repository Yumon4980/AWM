using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;
using AltTabReplacer.ViewModels;

namespace AltTabReplacer;

/// <summary>
/// 主选择器窗口。显示当前可见窗口的缩略图网格，
/// 接收 123QWE 物理键、鼠标移动 / 点击、Esc。
/// 由 <see cref="App"/> 创建和销毁。
/// </summary>
public partial class SelectorWindow : Window
{
    private readonly SelectorViewModel _vm = new();
    private readonly IReadOnlyList<WindowInfo> _raw;
    private readonly WindowActivator _activator;
    private readonly Settings _settings;
    private int _pageStart;   // 当前页起始索引（用于翻页）
    private int _maxPerPage = 35;

    // ---- 拖动状态 ----
    private DateTime _mouseDownTime;
    private System.Windows.Point _mouseDownPos;
    private int _mouseDownCellIndex = -1;     // 按下时所在的 cell index（-1 = 空白）
    private bool _isDragging;
    private bool _suppressNextClick;          // 拖动刚结束：屏蔽 OnMouseLeftButtonUp 的切窗
    private DispatcherTimer? _dragSafetyTimer;    // 轮询左键状态，兜底处理"窗口收不到 MouseUp"
    private DispatcherTimer? _dragFollowTimer;    // 轮询 OS 鼠标位置，让 ghost 鼠标出 Window 也跟随

    public SelectorWindow(IReadOnlyList<WindowInfo> windows, WindowCaptureService capture, Settings settings)
    {
        InitializeComponent();
        DataContext = _vm;

        _raw = windows;
        _activator = new WindowActivator();
        _settings = settings;
        _maxPerPage = settings.Layout.MaxPerPage;

        BuildCells(capture);

        // Show 后强制抢焦点（让按键 1/2/3 落到我们这里）
        // 注意：不要用 ImmAssociateContext(hwnd, NULL)，那会破坏整个线程的 IME 状态。
        // IME 拦截问题改用全局低层键盘钩子（LowLevelKeyboardHook）解决。
        Loaded += (_, __) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    private void BuildCells(WindowCaptureService capture)
    {
        _vm.Cells.Clear();
        int end = Math.Min(_pageStart + _maxPerPage, _raw.Count);
        for (int i = _pageStart; i < end; i++)
        {
            var w = _raw[i];
            // 关键：label 用 raw 内的全局索引（cells 重排后该 cell 的 label 不变）
            var label = i < Core.KeyMap.IndexToLabel.Length ? Core.KeyMap.IndexToLabel[i] : "?";
            BitmapSource? thumb = null;
            try
            {
                thumb = capture.Capture(w.Hwnd, _settings.Layout.CellWidth, _settings.Layout.CellHeight);
            }
            catch
            {
                // 单个窗口截图失败不影响整体
            }
            _vm.Cells.Add(new WindowCellViewModel(w, i, label, thumb));
        }
        _vm.SelectedIndex = 0;
    }

    // ----------------------------------------------------------
    //  输入处理
    // ----------------------------------------------------------

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 直接走 VK 路径（KeyMap 已经是 VK 索引）
        int vk = KeyInterop.VirtualKeyFromKey(e.Key);
        HandleVk(vk);
        e.Handled = true;
    }

    /// <summary>由全局低层键盘钩子调用（在 WPF 线程上）。</summary>
    public void HandleVk(int vk)
    {
        Logger.Info($"HandleVk: vk=0x{vk:X2} cells={_vm.Cells.Count}");

        // Esc = 取消
        if (vk == 0x1B)
        {
            Cancel();
            return;
        }

        // Tab / Shift+Tab = 翻页
        if (vk == 0x09)
        {
            bool next = (Keyboard.Modifiers & ModifierKeys.Shift) == 0;
            FlipPage(next);
            return;
        }

        // 123QWE 物理键
        if (Core.KeyMap.ToIndex(vk) is int localIndex && localIndex < _vm.Cells.Count)
        {
            ActivateAtAndClose(localIndex);
            return;
        }

        // 其它键：不响应（钩子层不会拦截，所以这些键会传给前台窗口）
    }

    private void ActivateAtAndClose(int localIndex)
    {
        if (localIndex < 0 || localIndex >= _vm.Cells.Count) return;
        var cell = _vm.Cells[localIndex];
        try
        {
            _activator.Activate(cell.Info);
        }
        catch (Exception ex)
        {
            Logger.Error($"按键激活窗口失败: {ex.Message}");
        }
        Close();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 保留以防 XAML 引用，但实际由 OnPreviewMouseMove 处理
    }

    private void OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isDragging || _suppressNextClick)
        {
            _isDragging = false;
            _suppressNextClick = false;
            return;   // 拖动刚结束 / 切窗已被前置处理
        }
        ConfirmAndClose();
    }

    // ----------------------------------------------------------
    //  长按拖动：背景拖窗口 / cell 拖动改排序
    // ----------------------------------------------------------

    private void OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _mouseDownTime = DateTime.Now;
        _mouseDownPos = e.GetPosition(this);
        _isDragging = false;
        StopDragSafetyTimer();

        // 判断按下点是否在某个 cell 上
        _mouseDownCellIndex = -1;
        var pos = e.GetPosition(PART_List);
        var hit = PART_List.InputHitTest(pos) as DependencyObject;
        if (hit != null)
        {
            var item = FindAncestor<ListBoxItem>(hit);
            if (item != null)
            {
                _mouseDownCellIndex = PART_List.ItemContainerGenerator.IndexFromContainer(item);
            }
        }
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // 1) 拖动中：跟手 + clamp 范围
        if (_isDragging)
        {
            if (_mouseDownCellIndex >= 0)
            {
                UpdateGhostFromOs();
            }
            return;
        }

        // 2) 鼠标按下：长按阈值判断是否进入拖动
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var elapsed = (DateTime.Now - _mouseDownTime).TotalMilliseconds;
            if (elapsed < 200) return;

            var pos = e.GetPosition(this);
            var dx = Math.Abs(pos.X - _mouseDownPos.X);
            var dy = Math.Abs(pos.Y - _mouseDownPos.Y);
            if (dx < 8 && dy < 8) return;

            _isDragging = true;
            if (_mouseDownCellIndex >= 0)
            {
                ShowDragGhost();
                UpdateGhostFromOs();
                StartDragSafetyTimer();   // 兜底：即使窗口收不到 MouseUp 也能恢复
            }
            else
            {
                try { DragMove(); }
                catch { _isDragging = false; }
            }
            return;
        }

        // 3) 鼠标未按下：hover 切高亮
        UpdateHoverHighlight(e);
    }

    private void UpdateHoverHighlight(System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(PART_List);
        var hit = PART_List.InputHitTest(pos) as DependencyObject;
        if (hit == null) return;
        var item = FindAncestor<ListBoxItem>(hit);
        if (item != null)
        {
            int idx = PART_List.ItemContainerGenerator.IndexFromContainer(item);
            if (idx >= 0 && idx != _vm.SelectedIndex)
            {
                _vm.SelectedIndex = idx;
            }
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        EndDrag();
    }

    private void EndDrag()
    {
        // 直接用 OS 屏幕坐标找最近 cell（不依赖 PointFromScreen 转换，避免 DPI / 多 Window 偏差）
        var screenPos = System.Windows.Forms.Cursor.Position;
        int target = _mouseDownCellIndex >= 0 ? FindNearestCellFromScreen(screenPos) : -1;
        HideDragGhost();
        StopDragSafetyTimer();

        if (_mouseDownCellIndex >= 0 && target >= 0 && target != _mouseDownCellIndex)
        {
            ReorderCell(_mouseDownCellIndex, target);
            OrderChanged?.Invoke(_vm.Cells);
            Logger.Info($"重排+持久化: {_mouseDownCellIndex} -> {target}");
        }
        _isDragging = false;
        _suppressNextClick = true;   // 屏蔽紧跟的切窗
    }

    // ----------------------------------------------------------
    //  Safety timer：兜底处理"窗口收不到 MouseUp"的情况
    //  每 33ms 轮询左键状态；若松开则强制结束拖动
    // ----------------------------------------------------------

    private void StartDragSafetyTimer()
    {
        if (_dragSafetyTimer != null) return;
        _dragSafetyTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _dragSafetyTimer.Tick += (_, __) =>
        {
            if (!_isDragging)
            {
                StopDragSafetyTimer();
                return;
            }
            // VK_LBUTTON = 0x01
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0)
            {
                Logger.Warn("通过 safety timer 检测到左键松开，强制结束拖动");
                EndDrag();
            }
        };
        _dragSafetyTimer.Start();
    }

    private void StopDragSafetyTimer()
    {
        _dragSafetyTimer?.Stop();
        _dragSafetyTimer = null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>用屏幕坐标找离 cursor 最近的 cell（按欧氏距离²）。不依赖 PointFromScreen 转换。</summary>
    private int FindNearestCellFromScreen(System.Drawing.Point screenPos)
    {
        double minDistSq = double.MaxValue;
        int nearest = -1;
        for (int i = 0; i < _vm.Cells.Count; i++)
        {
            var cell = PART_List.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (cell == null) continue;
            var cellCenter = cell.PointToScreen(new System.Windows.Point(cell.ActualWidth / 2, cell.ActualHeight / 2));
            double dx = cellCenter.X - screenPos.X;
            double dy = cellCenter.Y - screenPos.Y;
            double distSq = dx * dx + dy * dy;
            if (distSq < minDistSq) { minDistSq = distSq; nearest = i; }
        }
        return nearest;
    }

    private void ReorderCell(int from, int to)
    {
        if (from < 0 || from >= _vm.Cells.Count) return;
        if (to < 0 || to >= _vm.Cells.Count) return;
        if (from == to) return;

        var cell = _vm.Cells[from];
        _vm.Cells.RemoveAt(from);
        _vm.Cells.Insert(to, cell);
        _vm.SelectedIndex = to;

        // 关键：重排后让 KeyLabel 跟着位置走（每个 cell 显示它在 cells 里的新位置对应的按键）
        for (int i = 0; i < _vm.Cells.Count; i++)
        {
            _vm.Cells[i].KeyLabel = i < Core.KeyMap.IndexToLabel.Length
                ? Core.KeyMap.IndexToLabel[i]
                : "?";
        }
        Logger.Info($"重排: {from} -> {to} ({cell.Info.Title})");
    }

    // ----------------------------------------------------------
    //  Ghost 拖动特效：用独立 topmost 透明 Window（不是 Popup），
    //  Window.Left/Top 是 OS 屏幕坐标，中心精确对准鼠标
    // ----------------------------------------------------------

    private Window? _ghostWindow;
    private Border? _ghostBorder;

    private void ShowDragGhost()
    {
        if (_mouseDownCellIndex < 0 || _mouseDownCellIndex >= _vm.Cells.Count) return;
        var cell = _vm.Cells[_mouseDownCellIndex];

        // 创建独立 topmost 透明 Window
        _ghostBorder = BuildGhostContent(cell);
        _ghostWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            IsHitTestVisible = false,
            Focusable = false,
            Width = 256,
            Height = 180,
            Content = _ghostBorder,
        };
        // 初始位置：ghost 中心 = 鼠标
        var sp = System.Windows.Forms.Cursor.Position;
        var (dipX, dipY) = ScreenPxToDip(sp);
        _ghostWindow.Left = dipX - 128;
        _ghostWindow.Top = dipY - 90;
        _ghostWindow.Show();

        StartDragFollowTimer();
    }

    /// <summary>从 OS 全局鼠标位置更新 ghost（鼠标出 Window 也能跟随）。</summary>
    private void UpdateGhostFromOs()
    {
        if (_ghostWindow == null) return;
        var sp = System.Windows.Forms.Cursor.Position;     // 物理像素
        // 物理像素 → WPF DIPs，然后偏移半宽半高让 ghost 中心对准鼠标
        var (dipX, dipY) = ScreenPxToDip(sp);
        _ghostWindow.Left = dipX - 128;
        _ghostWindow.Top  = dipY - 90;
    }

    /// <summary>物理像素 → WPF DIPs（用 _ghostWindow 的 CompositionTarget）。</summary>
    private (double x, double y) ScreenPxToDip(System.Drawing.Point sp)
    {
        if (_ghostWindow == null) return (sp.X, sp.Y);
        var helper = new System.Windows.Interop.WindowInteropHelper(_ghostWindow);
        if (helper.Handle == IntPtr.Zero) return (sp.X, sp.Y);
        var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
        if (source == null) return (sp.X, sp.Y);
        var dip = source.CompositionTarget.TransformFromDevice.Transform(
            new System.Windows.Point(sp.X, sp.Y));
        return (dip.X, dip.Y);
    }

    private void HideDragGhost()
    {
        StopDragFollowTimer();
        if (_ghostWindow != null)
        {
            _ghostWindow.Close();
            _ghostWindow = null;
        }
        _ghostBorder = null;
    }

    private void StartDragFollowTimer()
    {
        if (_dragFollowTimer != null) return;
        _dragFollowTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),  // 60Hz
        };
        _dragFollowTimer.Tick += (_, __) =>
        {
            if (!_isDragging) { StopDragFollowTimer(); return; }
            UpdateGhostFromOs();
        };
        _dragFollowTimer.Start();
    }

    private void StopDragFollowTimer()
    {
        _dragFollowTimer?.Stop();
        _dragFollowTimer = null;
    }

    private static Border BuildGhostContent(WindowCellViewModel cell)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(26, 26, 26)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)),
            BorderThickness = new Thickness(2),
            Opacity = 0.85,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                Opacity = 0.7,
                ShadowDepth = 6,
                Color = Colors.Black,
            },
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        if (cell.Thumbnail is BitmapSource thumb)
        {
            grid.Children.Add(new System.Windows.Controls.Image
            {
                Source = thumb,
                Stretch = Stretch.UniformToFill,
            });
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = "(no preview)",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
        }

        var title = new TextBlock
        {
            Text = cell.Title,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 221, 221)),
            FontSize = 12,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(10, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(title, 1);
        grid.Children.Add(title);

        border.Child = grid;
        return border;
    }

    /// <summary>用户重排后触发；App 订阅以持久化到 RuleStore。</summary>
    public event Action<IReadOnlyList<WindowCellViewModel>>? OrderChanged;

    /// <summary>让 App 能在 Closed 时读取当前顺序（不依赖事件丢失）。</summary>
    public IReadOnlyList<WindowCellViewModel> CurrentCells => _vm.Cells;

    // ----------------------------------------------------------
    //  外部调用
    // ----------------------------------------------------------

    /// <summary>切到当前选中窗口并关闭（保留作 API 兼容，目前 Quick-Switcher 模式不调用）。</summary>
    public void ConfirmAndClose()
    {
        Logger.Info($"SelectorWindow.ConfirmAndClose: cells={_vm.Cells.Count} selectedIndex={_vm.SelectedIndex}");
        if (_vm.Cells.Count == 0)
        {
            Close();
            return;
        }
        var selected = _vm.Cells[_vm.SelectedIndex];
        try
        {
            _activator.Activate(selected.Info);
        }
        finally
        {
            Logger.Info("SelectorWindow.ConfirmAndClose -> Close()");
            Close();
        }
    }

    /// <summary>取消：仅关闭，不切窗（Quick-Switcher 模式下 Alt+Z 二次 / Esc 走这里）。</summary>
    public void Cancel()
    {
        Logger.Info("SelectorWindow.Cancel -> Close()");
        Close();
    }

    private void FlipPage(bool next)
    {
        if (next)
        {
            if (_pageStart + _maxPerPage >= _raw.Count) return;
            _pageStart += _maxPerPage;
        }
        else
        {
            if (_pageStart == 0) return;
            _pageStart -= _maxPerPage;
        }
        BuildCells(GetCaptureService());
    }

    private WindowCaptureService GetCaptureService()
    {
        // 简化：从 App 单例拿（个人自用工具，足够）
        return ((App)System.Windows.Application.Current).CaptureService!;
    }

    // ----------------------------------------------------------
    //  辅助
    // ----------------------------------------------------------

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
