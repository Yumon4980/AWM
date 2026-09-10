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
/// 主选择器窗口：双列布局
///   左：搜索框 + 列表（图标 + 标题 + 索引）
///   右：实时预览当前选中窗口
/// 由 App 创建和销毁。
/// </summary>
public partial class SelectorWindow : Window
{
    private readonly SelectorViewModel _vm = new();
    private readonly IReadOnlyList<WindowInfo> _raw;
    private readonly WindowActivator _activator;
    private readonly WindowCaptureService _capture;
    private readonly Settings _settings;
    private int _pageStart;
    private int _maxPerPage = 35;

    // ---- 拖动状态 ----
    private DateTime _mouseDownTime;
    private System.Windows.Point _mouseDownPos;
    private int _mouseDownCellIndex = -1;
    private bool _isDragging;
    private bool _suppressNextClick;
    private DispatcherTimer? _dragSafetyTimer;
    private DispatcherTimer? _dragFollowTimer;

    // ---- Ghost ----
    private Window? _ghostWindow;

    public SelectorWindow(IReadOnlyList<WindowInfo> windows, WindowCaptureService capture, Settings settings)
    {
        InitializeComponent();
        DataContext = _vm;

        _raw = windows;
        _activator = new WindowActivator();
        _capture = capture;
        _settings = settings;
        _maxPerPage = settings.Layout.MaxPerPage;

        BuildCells(capture);

        Loaded += (_, __) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(PART_SearchBox);
        };
    }

    private void BuildCells(WindowCaptureService capture)
    {
        _vm.Cells.Clear();
        int end = Math.Min(_pageStart + _maxPerPage, _raw.Count);
        for (int i = _pageStart; i < end; i++)
        {
            var w = _raw[i];
            var label = i < Core.KeyMap.IndexToLabel.Length ? Core.KeyMap.IndexToLabel[i] : "?";
            BitmapSource? thumb = null;
            BitmapSource? icon = null;
            try { thumb = capture.Capture(w.Hwnd, _settings.Layout.CellWidth, _settings.Layout.CellHeight); } catch { }
            try { icon = GetAppIcon(w.Hwnd); } catch { }
            _vm.Cells.Add(new WindowCellViewModel(w, i, label, thumb, icon));
        }
        if (_vm.Cells.Count > 0) _vm.SelectedCell = _vm.Cells[0];
        UpdatePreview();
    }

    /// <summary>从 hwnd 提取应用图标（WM_GETICON → BitmapSource）。</summary>
    private static BitmapSource? GetAppIcon(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        const uint WM_GETICON = 0x007F;
        IntPtr hIcon = SendMessage(hwnd, WM_GETICON, (IntPtr)1, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) hIcon = SendMessage(hwnd, WM_GETICON, (IntPtr)0, IntPtr.Zero);
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            using var bmp = icon.ToBitmap();
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DeleteObject(hBitmap); }
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    // ----------------------------------------------------------
    //  搜索 + 预览
    // ----------------------------------------------------------

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _vm.SearchText = PART_SearchBox.Text;
        PART_SearchHint.Visibility = string.IsNullOrEmpty(PART_SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        // 搜索后默认选中第一个
        if (_vm.FilteredCells.Cast<object>().FirstOrDefault() is WindowCellViewModel first)
            _vm.SelectedCell = first;
        UpdatePreview();
    }

    private void OnSearchBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 在搜索框内按 Esc 清除，按 Down 移到列表
        if (e.Key == Key.Escape)
        {
            PART_SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            PART_List.Focus();
            e.Handled = true;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePreview();
    }

    /// <summary>捕获当前选中窗口并显示在右侧预览区。</summary>
    private void UpdatePreview()
    {
        if (_vm.SelectedCell == null)
        {
            PART_PreviewImage.Source = null;
            PART_PreviewPlaceholder.Visibility = Visibility.Visible;
            PART_PreviewTitle.Text = "";
            return;
        }

        PART_PreviewTitle.Text = _vm.SelectedCell.Title;
        try
        {
            var bmp = _capture.Capture(_vm.SelectedCell.Info.Hwnd, 0, 0);  // 0,0 = 原始大小
            if (bmp != null)
            {
                PART_PreviewImage.Source = bmp;
                PART_PreviewPlaceholder.Visibility = Visibility.Collapsed;
            }
            else
            {
                PART_PreviewImage.Source = null;
                PART_PreviewPlaceholder.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            PART_PreviewImage.Source = null;
            PART_PreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    // ----------------------------------------------------------
    //  输入处理：键盘 123QWE / 鼠标 hover
    // ----------------------------------------------------------

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        int vk = KeyInterop.VirtualKeyFromKey(e.Key);
        HandleVk(vk);
    }

    /// <summary>由全局低层键盘钩子调用（在 WPF 线程上）。</summary>
    public void HandleVk(int vk)
    {
        if (vk == 0x1B) { Cancel(); return; }   // Esc
        if (vk == 0x09) { FlipPage((Keyboard.Modifiers & ModifierKeys.Shift) == 0); return; }  // Tab

        if (Core.KeyMap.ToIndex(vk) is int localIndex)
        {
            // 数字键在 FilteredCells 中找对应 cell
            var filtered = _vm.FilteredCells.Cast<WindowCellViewModel>().ToList();
            if (localIndex < filtered.Count)
            {
                ActivateCellAndClose(filtered[localIndex]);
            }
            return;
        }
    }

    private void ActivateCellAndClose(WindowCellViewModel cell)
    {
        try { _activator.Activate(cell.Info); }
        catch (Exception ex) { Logger.Error($"按键激活失败: {ex.Message}"); }
        Close();
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isDragging)
        {
            if (_mouseDownCellIndex >= 0) UpdateGhostFromOs();
            return;
        }

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
                StartDragSafetyTimer();
            }
            else
            {
                try { DragMove(); } catch { _isDragging = false; }
            }
            return;
        }

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
            var cell = item.DataContext as WindowCellViewModel;
            if (cell != null && cell != _vm.SelectedCell)
            {
                _vm.SelectedCell = cell;
            }
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        EndDrag();
    }

    private void OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isDragging || _suppressNextClick)
        {
            _isDragging = false;
            _suppressNextClick = false;
            return;
        }
        // 单击：切到选中窗口
        if (_vm.SelectedCell != null)
        {
            try { _activator.Activate(_vm.SelectedCell.Info); } catch { }
            Close();
        }
        else
        {
            Cancel();
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _mouseDownTime = DateTime.Now;
        _mouseDownPos = e.GetPosition(this);
        _isDragging = false;
        StopDragSafetyTimer();

        // 判断按下点是否在列表中某行
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

    private void EndDrag()
    {
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
        _suppressNextClick = true;
    }

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
        // KeyLabel 跟着位置走
        for (int i = 0; i < _vm.Cells.Count; i++)
        {
            _vm.Cells[i].KeyLabel = i < Core.KeyMap.IndexToLabel.Length
                ? Core.KeyMap.IndexToLabel[i] : "?";
        }
        Logger.Info($"重排: {from} -> {to} ({cell.Info.Title})");
    }

    // ----------------------------------------------------------
    //  Ghost 拖动特效
    // ----------------------------------------------------------

    private void ShowDragGhost()
    {
        if (_mouseDownCellIndex < 0 || _mouseDownCellIndex >= _vm.Cells.Count) return;
        var cell = _vm.Cells[_mouseDownCellIndex];
        var border = BuildGhostContent(cell);
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
            Width = 256, Height = 180,
            Content = border,
        };
        var sp = System.Windows.Forms.Cursor.Position;
        var (dipX, dipY) = ScreenPxToDip(sp);
        _ghostWindow.Left = dipX - 128;
        _ghostWindow.Top = dipY - 90;
        _ghostWindow.Show();
        StartDragFollowTimer();
    }

    private void UpdateGhostFromOs()
    {
        if (_ghostWindow == null) return;
        var sp = System.Windows.Forms.Cursor.Position;
        var (dipX, dipY) = ScreenPxToDip(sp);
        _ghostWindow.Left = dipX - 128;
        _ghostWindow.Top  = dipY - 90;
    }

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
        if (_ghostWindow != null) { _ghostWindow.Close(); _ghostWindow = null; }
    }

    private void StartDragFollowTimer()
    {
        if (_dragFollowTimer != null) return;
        _dragFollowTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
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
                BlurRadius = 20, Opacity = 0.7, ShadowDepth = 6, Color = Colors.Black,
            },
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        // 拖动 ghost 只显示应用图标（不要实时渲染图，性能更好 + 视觉更轻）
        if (cell.Icon is BitmapSource icon)
        {
            grid.Children.Add(new System.Windows.Controls.Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                Width = 96,
                Height = 96,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = "(no icon)",
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

    // ----------------------------------------------------------
    //  Safety timer
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
            if (!_isDragging) { StopDragSafetyTimer(); return; }
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0)
            {
                Logger.Warn("safety timer 检测到松手，强制结束拖动");
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

    // ----------------------------------------------------------
    //  切窗 / 取消 / 翻页
    // ----------------------------------------------------------

    /// <summary>切到当前选中窗口并关闭（保留作 API 兼容）。</summary>
    public void ConfirmAndClose()
    {
        if (_vm.SelectedCell != null)
        {
            try { _activator.Activate(_vm.SelectedCell.Info); } catch { }
        }
        Close();
    }

    public void Cancel() => Close();

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

    private WindowCaptureService GetCaptureService() => ((App)System.Windows.Application.Current).CaptureService!;

    // ----------------------------------------------------------
    //  事件
    // ----------------------------------------------------------

    public event Action<IReadOnlyList<WindowCellViewModel>>? OrderChanged;
    public IReadOnlyList<WindowCellViewModel> CurrentCells => _vm.Cells;

    // ----------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
