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
using AltTabReplacer.Core;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;
using AltTabReplacer.ViewModels;

namespace AltTabReplacer;

/// <summary>
/// 主选择器窗口：双列布局
///   左：面包屑 + 槽位列表（16 键，一级可以是窗口也可以是组）
///   右：实时预览当前选中窗口
///
/// 两级导航见 <see cref="OnWindowPreviewKeyDown"/> 与 <see cref="HandleVk"/>。
/// 由 App 创建和销毁。
/// </summary>
public partial class SelectorWindow : Window
{
    private readonly SelectorViewModel _vm = new();
    private readonly IReadOnlyList<WindowInfo> _rawWindows;
    private readonly WindowActivator _activator;
    private readonly WindowCaptureService _capture;
    private readonly Settings _settings;

    /// <summary>一级槽位（解析结果）。二级从其中某个组展开。</summary>
    private IReadOnlyList<ResolvedSlot> _topSlots;

    /// <summary>当前进入的组；null 表示在一级。</summary>
    private ResolvedSlot? _openGroup;
    private int _openGroupIndex = -1;

    /// <summary>进入组之前一级的高亮项，Esc 回退时恢复。</summary>
    private int _savedTopSelection;

    private readonly Dictionary<IntPtr, BitmapSource?> _iconCache = new();

    // ---- 拖动状态 ----
    private DateTime _mouseDownTime;
    private System.Windows.Point _mouseDownPos;
    private int _dragSourceIndex = -1;
    private bool _isDragging;
    private bool _suppressNextClick;
    private DispatcherTimer? _dragSafetyTimer;
    private DispatcherTimer? _dragFollowTimer;
    private DropTarget _currentDrop = DropTarget.None;

    // ---- Ghost ----
    private Window? _ghostWindow;

    // ---- 关闭状态 ----
    private bool _closing;
    private bool _everActivated;

    /// <summary>布局变了（拖动产生分组/排序），App 负责落盘。</summary>
    public event Action<IReadOnlyList<ResolvedSlot>>? LayoutChanged;

    /// <summary>搜索模式开关。App 据此让低层钩子放行索引键。</summary>
    public event Action<bool>? SearchModeChanged;

    public SelectorWindow(IReadOnlyList<WindowInfo> windows, IReadOnlyList<ResolvedSlot> slots,
        WindowCaptureService capture, Settings settings)
    {
        InitializeComponent();
        DataContext = _vm;

        _rawWindows = windows;
        _topSlots = slots;
        _activator = new WindowActivator();
        _capture = capture;
        _settings = settings;

        BuildRows(_topSlots);

        SourceInitialized += (_, __) =>
        {
            ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!).AddHook(WndProc);
            ApplyPrimaryScreenGeometry();
        };

        Loaded += (_, __) =>
        {
            ApplyPrimaryScreenGeometry();
            WindowActivator.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Activate();
            Focus();
        };

        Activated += (_, __) => _everActivated = true;

        Deactivated += (_, __) =>
        {
            if (!_everActivated) return;
            Logger.Info("选择器失焦，自动关闭");
            Cancel();
        };
    }

    // ----------------------------------------------------------
    //  窗口几何 / 系统菜单
    // ----------------------------------------------------------

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 唤起后 Alt 通常还按着，任何 Alt+键都会被 DefWindowProc 解释成"激活窗口菜单"
        if (msg == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_KEYMENU)
        {
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private const double MinSelectorWidth = 640;
    private const double MinSelectorHeight = 420;

    /// <summary>
    /// 尺寸和位置都从主显示器工作区算：尺寸取固定比例（与分辨率解耦），位置取正中。
    /// 不用 WindowStartupLocation="CenterScreen"，多显示器下它居中到的不一定是主显示器。
    /// </summary>
    private void ApplyPrimaryScreenGeometry()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;

        var wa = screen.WorkingArea;
        var m = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!)
            .CompositionTarget!.TransformFromDevice;

        var origin = m.Transform(new System.Windows.Point(wa.Left, wa.Top));
        var extent = m.Transform(new System.Windows.Vector(wa.Width, wa.Height));

        double w = Fit(extent.X, _settings.Layout.WidthRatio, MinSelectorWidth);
        double h = Fit(extent.Y, _settings.Layout.HeightRatio, MinSelectorHeight);

        Width = w;
        Height = h;
        Left = origin.X + (extent.X - w) / 2;
        Top = origin.Y + (extent.Y - h) / 2;
    }

    private static double Fit(double available, double ratio, double min)
    {
        double v = available * ratio;
        if (v < min) v = min;
        if (v > available) v = available;
        return v;
    }

    // ----------------------------------------------------------
    //  行构建
    // ----------------------------------------------------------

    private void BuildRows(IReadOnlyList<ResolvedSlot> slots)
    {
        _vm.Slots.Clear();
        for (int i = 0; i < slots.Count; i++)
        {
            _vm.Slots.Add(MakeRow(slots[i], KeyMap.LabelOf(i)));
        }
        _vm.SelectedSlot = _vm.Slots.FirstOrDefault();
        UpdatePreview();
    }

    private SlotViewModel MakeRow(ResolvedSlot slot, string label)
    {
        BitmapSource? icon = slot.Count > 0 ? IconFor(slot.Windows[0]) : null;
        var icons = slot.Kind == SlotKind.Group
            ? slot.Windows.Take(4).Select(IconFor).ToList()
            : new List<BitmapSource?>();
        return new SlotViewModel(slot, label, icon, icons);
    }

    /// <summary>把组内成员展开成二级的行（每个成员一个窗口行）。</summary>
    private void BuildGroupRows(ResolvedSlot group)
    {
        _vm.Slots.Clear();
        for (int i = 0; i < group.Windows.Count && i < KeyMap.Size; i++)
        {
            var w = group.Windows[i];
            var leaf = new ResolvedSlot
            {
                Kind = SlotKind.Window,
                Name = w.Title,
                Windows = new[] { w },
                Processes = new[] { w.ProcessName },
            };
            _vm.Slots.Add(MakeRow(leaf, KeyMap.LabelOf(i)));
        }
        _vm.SelectedSlot = _vm.Slots.FirstOrDefault();
        UpdatePreview();
    }

    private BitmapSource? IconFor(WindowInfo w)
    {
        if (_iconCache.TryGetValue(w.Hwnd, out var cached)) return cached;

        BitmapSource? icon = null;
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            hIcon = IconExtractor.GetWindowIcon(w.Hwnd, w.ProcessId);
            if (hIcon != IntPtr.Zero) icon = HIconToBitmapSource(hIcon);
        }
        catch (Exception ex)
        {
            Logger.Warn($"提取图标失败 ({w.ProcessName}): {ex.Message}");
        }
        finally
        {
            // IconExtractor 保证返回的句柄归调用方所有，这里统一释放
            IconExtractor.Release(hIcon);
        }

        _iconCache[w.Hwnd] = icon;
        return icon;
    }

    private static BitmapSource? HIconToBitmapSource(IntPtr hIcon)
    {
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(hIcon);
            using var bmp = icon.ToBitmap();
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DeleteObject(hBitmap); }
        }
        catch { return null; }
    }

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
        if (_vm.FilteredSlots.Cast<object>().FirstOrDefault() is SlotViewModel first)
            _vm.SelectedSlot = first;
        UpdatePreview();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    /// <summary>捕获当前选中项的代表窗口并显示在右侧预览区。</summary>
    private void UpdatePreview()
    {
        var rep = _vm.SelectedSlot?.Representative;
        if (rep == null)
        {
            PART_PreviewImage.Source = null;
            PART_PreviewPlaceholder.Visibility = Visibility.Visible;
            PART_PreviewTitle.Text = "";
            return;
        }

        PART_PreviewTitle.Text = _vm.SelectedSlot!.Slot.Kind == SlotKind.Group
            ? $"{_vm.SelectedSlot.Slot.Name} — {rep.Title}"
            : rep.Title;
        try
        {
            var bmp = _capture.Capture(rep.Hwnd, 0, 0);
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
    //  键盘：两级导航
    // ----------------------------------------------------------

    /// <summary>
    /// 窗口级 PreviewKeyDown —— 导航键必须在这里截。
    /// 焦点在列表上，`Tab` 会被 WPF 的焦点导航吃掉，冒泡的 KeyDown 根本收不到。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Alt 还按着时 WPF 把按键报成 Key.System，真正的键在 SystemKey 里
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        switch (key)
        {
            case Key.Escape:
                e.Handled = true;
                GoBack();                       // 逐级回退，到顶了才关闭
                return;

            case Key.Back:
                if (_vm.Level != SelectorLevel.Search)   // 搜索时 Backspace 要能删字
                {
                    e.Handled = true;
                    GoBack();
                    return;
                }
                break;

            case Key.Enter:
                e.Handled = true;
                ConfirmAndClose();
                return;

            case Key.Down:
                e.Handled = true;
                MoveSelection(1);
                return;

            case Key.Up:
                e.Handled = true;
                MoveSelection(-1);
                return;

            case Key.Tab:
                e.Handled = true;
                MoveSelection((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                return;

            case Key.Oem2:                      // "/" 进入搜索模式
                if (_vm.Level != SelectorLevel.Search && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    e.Handled = true;
                    EnterSearch();
                    return;
                }
                break;
        }

        // 其余 Alt+键一律吃掉，否则 DefWindowProc 会去激活窗口菜单并抢走焦点
        if (e.Key == Key.System) e.Handled = true;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_vm.Level == SelectorLevel.Search) return;   // 搜索时按键交给 TextBox
        HandleVk(KeyInterop.VirtualKeyFromKey(e.Key));
    }

    /// <summary>
    /// 索引键分派。由全局低层键盘钩子调用（已投回 WPF 线程），也作为 OnKeyDown 的兜底。
    ///
    /// 一级：命中窗口 → 直接切；命中组 → 进入二级
    /// 二级：命中成员 → 直接切；没有映射 → 忽略
    /// </summary>
    public void HandleVk(int vk)
    {
        if (_vm.Level == SelectorLevel.Search) return;
        if (KeyMap.ToIndex(vk) is not int index) return;

        var rows = _vm.FilteredSlots.Cast<SlotViewModel>().ToList();
        if (index >= rows.Count) return;        // 没有映射的键：忽略，不做任何事

        var row = rows[index];

        if (_vm.Level == SelectorLevel.Top && row.Slot.Kind == SlotKind.Group)
        {
            EnterGroup(index, row.Slot);
            return;
        }

        ActivateAndClose(row.Slot);
    }

    /// <summary>进入某个组（二级）。</summary>
    private void EnterGroup(int index, ResolvedSlot group)
    {
        _savedTopSelection = index;
        _openGroup = group;
        _openGroupIndex = index;
        _vm.Level = SelectorLevel.InGroup;
        _vm.Breadcrumb = $"{KeyMap.LabelOf(index)} › {group.Name}";
        BuildGroupRows(group);
        Logger.Info($"进入组: {group.Name} ({group.Count} 个窗口)");
    }

    /// <summary>
    /// Esc / Backspace 的逐级回退。
    /// 搜索 → 一级；二级 → 一级（并恢复进入前的高亮）；一级 → 关闭选择器。
    /// 这就是"按 Esc 撤销刚才按下的那个组键"。
    /// </summary>
    private void GoBack()
    {
        switch (_vm.Level)
        {
            case SelectorLevel.Search:
                ExitSearch();
                return;

            case SelectorLevel.InGroup:
                _openGroup = null;
                _openGroupIndex = -1;
                _vm.Level = SelectorLevel.Top;
                _vm.Breadcrumb = "";
                BuildRows(_topSlots);
                if (_savedTopSelection >= 0 && _savedTopSelection < _vm.Slots.Count)
                {
                    _vm.SelectedSlot = _vm.Slots[_savedTopSelection];
                    PART_List.ScrollIntoView(_vm.SelectedSlot);
                }
                return;

            default:
                Cancel();
                return;
        }
    }

    private void EnterSearch()
    {
        _vm.Level = SelectorLevel.Search;
        _vm.Breadcrumb = "搜索";
        // 扁平列出所有窗口，跨组搜索才有意义
        _vm.Slots.Clear();
        for (int i = 0; i < _rawWindows.Count; i++)
        {
            var w = _rawWindows[i];
            var leaf = new ResolvedSlot
            {
                Kind = SlotKind.Window,
                Name = w.Title,
                Windows = new[] { w },
                Processes = new[] { w.ProcessName },
            };
            _vm.Slots.Add(MakeRow(leaf, ""));
        }
        _vm.SearchText = "";
        PART_SearchBox.Text = "";
        _vm.SelectedSlot = _vm.Slots.FirstOrDefault();
        SearchModeChanged?.Invoke(true);        // 让钩子放行索引键，否则打不进字
        Dispatcher.BeginInvoke(() => Keyboard.Focus(PART_SearchBox));
        UpdatePreview();
    }

    private void ExitSearch()
    {
        SearchModeChanged?.Invoke(false);
        _vm.SearchText = "";
        PART_SearchBox.Text = "";
        _vm.Level = SelectorLevel.Top;
        _vm.Breadcrumb = "";
        BuildRows(_topSlots);
        Focus();
    }

    /// <summary>在当前层级的行之间移动选中项，到头到尾循环。</summary>
    private void MoveSelection(int delta)
    {
        var rows = _vm.FilteredSlots.Cast<SlotViewModel>().ToList();
        if (rows.Count == 0) return;

        int cur = _vm.SelectedSlot != null ? rows.IndexOf(_vm.SelectedSlot) : -1;
        int next = cur < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : ((cur + delta) % rows.Count + rows.Count) % rows.Count;

        _vm.SelectedSlot = rows[next];
        PART_List.ScrollIntoView(_vm.SelectedSlot);
    }

    // ----------------------------------------------------------
    //  激活 / 关闭
    // ----------------------------------------------------------

    /// <summary>确认当前选中项：组就进去，窗口就切过去。</summary>
    public void ConfirmAndClose()
    {
        var sel = _vm.SelectedSlot;
        if (sel == null) { Cancel(); return; }

        if (_vm.Level == SelectorLevel.Top && sel.Slot.Kind == SlotKind.Group)
        {
            EnterGroup(_vm.Slots.IndexOf(sel), sel.Slot);
            return;
        }
        ActivateAndClose(sel.Slot);
    }

    private void ActivateAndClose(ResolvedSlot slot)
    {
        var target = slot.SingleWindow;
        if (target == null) { Cancel(); return; }
        if (_closing) return;

        // 先置位再激活：切到目标窗口会让本窗口 Deactivated，别让它抢在前面重入 Close
        _closing = true;
        try { _activator.Activate(target); }
        catch (Exception ex) { Logger.Error($"激活失败: {ex.Message}"); }
        Close();
    }

    /// <summary>关闭但不切窗。所有"放弃"路径的唯一出口，重入安全。</summary>
    public void Cancel()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    // ----------------------------------------------------------
    //  鼠标 / 拖动
    // ----------------------------------------------------------

    /// <summary>拖放落点的语义。</summary>
    private enum DropMode { None, InsertBefore, InsertAfter, IntoSlot, OutOfGroup }

    private readonly record struct DropTarget(int Index, DropMode Mode)
    {
        public static readonly DropTarget None = new(-1, DropMode.None);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _mouseDownTime = DateTime.Now;
        _mouseDownPos = e.GetPosition(this);
        _isDragging = false;
        StopDragSafetyTimer();

        _dragSourceIndex = -1;
        var hit = PART_List.InputHitTest(e.GetPosition(PART_List)) as DependencyObject;
        if (hit != null && FindAncestor<ListBoxItem>(hit) is ListBoxItem item)
        {
            _dragSourceIndex = PART_List.ItemContainerGenerator.IndexFromContainer(item);
        }
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isDragging)
        {
            if (_dragSourceIndex >= 0)
            {
                UpdateGhostFromOs();
                UpdateDropFeedback();
            }
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if ((DateTime.Now - _mouseDownTime).TotalMilliseconds < 200) return;
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _mouseDownPos.X) < 8 && Math.Abs(pos.Y - _mouseDownPos.Y) < 8) return;

            _isDragging = true;
            if (_dragSourceIndex >= 0 && _vm.Level != SelectorLevel.Search)
            {
                ShowDragGhost();
                UpdateGhostFromOs();
                UpdateDropFeedback();
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
        var hit = PART_List.InputHitTest(e.GetPosition(PART_List)) as DependencyObject;
        if (hit == null) return;
        if (FindAncestor<ListBoxItem>(hit)?.DataContext is SlotViewModel cell && cell != _vm.SelectedSlot)
        {
            _vm.SelectedSlot = cell;
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
        ConfirmAndClose();
    }

    /// <summary>
    /// 落点分区：每行垂直切三段，位置决定语义。
    ///   上 25% → 插到本行之前（排序）
    ///   中 50% → 并入本行（分组）
    ///   下 25% → 插到本行之后（排序）
    /// Shift 强制排序、Ctrl 强制分组，作为高级用户的逃生通道。
    /// 二级时拖到面包屑上 = 移出该组。
    /// </summary>
    private DropTarget HitTestDrop(System.Drawing.Point screenPos)
    {
        // 二级：面包屑是"移出该组"的落点
        if (_vm.Level == SelectorLevel.InGroup && IsOverElement(PART_Breadcrumb, screenPos))
        {
            return new DropTarget(-1, DropMode.OutOfGroup);
        }
        if (_vm.Level != SelectorLevel.Top) return DropTarget.None;

        for (int i = 0; i < _vm.Slots.Count; i++)
        {
            if (PART_List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            if (item.ActualHeight <= 0) continue;

            var topLeft = item.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = item.PointToScreen(new System.Windows.Point(item.ActualWidth, item.ActualHeight));
            if (screenPos.Y < topLeft.Y || screenPos.Y >= bottomRight.Y) continue;

            double rel = (screenPos.Y - topLeft.Y) / (bottomRight.Y - topLeft.Y);
            bool forceOrder = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool forceGroup = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (forceGroup) return new DropTarget(i, DropMode.IntoSlot);
            if (forceOrder) return new DropTarget(i, rel < 0.5 ? DropMode.InsertBefore : DropMode.InsertAfter);
            if (rel < 0.25) return new DropTarget(i, DropMode.InsertBefore);
            if (rel > 0.75) return new DropTarget(i, DropMode.InsertAfter);
            return new DropTarget(i, DropMode.IntoSlot);
        }

        // 落在列表空白处 → 追加到末尾
        if (_vm.Slots.Count > 0 && IsOverElement(PART_List, screenPos))
            return new DropTarget(_vm.Slots.Count - 1, DropMode.InsertAfter);

        return DropTarget.None;
    }

    private bool IsOverElement(FrameworkElement el, System.Drawing.Point screenPos)
    {
        if (el.ActualWidth <= 0 || el.ActualHeight <= 0 || !el.IsVisible) return false;
        var tl = el.PointToScreen(new System.Windows.Point(0, 0));
        var br = el.PointToScreen(new System.Windows.Point(el.ActualWidth, el.ActualHeight));
        return screenPos.X >= tl.X && screenPos.X < br.X && screenPos.Y >= tl.Y && screenPos.Y < br.Y;
    }

    /// <summary>排序画插入线，分组画整行描边——两种反馈形状完全不同，不会看混。</summary>
    private void UpdateDropFeedback()
    {
        var drop = HitTestDrop(System.Windows.Forms.Cursor.Position);
        _currentDrop = drop;

        foreach (var s in _vm.Slots) s.IsDropTarget = false;
        _vm.BreadcrumbIsDropTarget = drop.Mode == DropMode.OutOfGroup;
        PART_InsertLine.Visibility = Visibility.Collapsed;

        if (drop.Mode == DropMode.IntoSlot && drop.Index >= 0 && drop.Index < _vm.Slots.Count)
        {
            // 自己并入自己没有意义
            if (drop.Index != _dragSourceIndex) _vm.Slots[drop.Index].IsDropTarget = true;
            return;
        }

        if (drop.Mode is DropMode.InsertBefore or DropMode.InsertAfter && drop.Index >= 0)
        {
            if (PART_List.ItemContainerGenerator.ContainerFromIndex(drop.Index) is not ListBoxItem item) return;
            // 转到 PART_ListHost（PART_List 与 PART_DropLayer 的共同父级）。
            // 不能转 PART_DropLayer —— 它是兄弟不是祖先，TransformToAncestor 会抛异常。
            var p = item.TransformToAncestor(PART_ListHost).Transform(new System.Windows.Point(0, 0));
            double y = drop.Mode == DropMode.InsertBefore ? p.Y : p.Y + item.ActualHeight;

            PART_InsertLine.Width = item.ActualWidth;
            Canvas.SetLeft(PART_InsertLine, p.X);
            Canvas.SetTop(PART_InsertLine, y - 4);
            PART_InsertLine.Visibility = Visibility.Visible;
        }
    }

    private void ClearDropFeedback()
    {
        foreach (var s in _vm.Slots) s.IsDropTarget = false;
        _vm.BreadcrumbIsDropTarget = false;
        PART_InsertLine.Visibility = Visibility.Collapsed;
    }

    private void EndDrag()
    {
        var drop = _currentDrop;
        int source = _dragSourceIndex;

        HideDragGhost();
        StopDragSafetyTimer();
        ClearDropFeedback();

        _isDragging = false;
        _suppressNextClick = true;
        _currentDrop = DropTarget.None;

        if (source < 0 || drop.Mode == DropMode.None) return;
        ApplyDrop(source, drop);
    }

    /// <summary>把一次落点变成新的槽位结构，并通知 App 落盘。结构编辑本身在 <see cref="SlotEditor"/> 里。</summary>
    private void ApplyDrop(int source, DropTarget drop)
    {
        // ---- 二级：拖到面包屑 = 把这个成员的进程移出该组 ----
        if (drop.Mode == DropMode.OutOfGroup)
        {
            if (_openGroup == null || _openGroupIndex < 0) return;
            if (source >= _openGroup.Windows.Count) return;

            var moved = _openGroup.Windows[source];
            var result = SlotEditor.MoveOutOfGroup(_topSlots, _openGroupIndex, moved.ProcessName);
            if (result == null)
            {
                Logger.Info($"该组只有 {moved.ProcessName} 一个进程，无法移出单个窗口");
                return;
            }
            Logger.Info($"移出组: {moved.ProcessName}");
            CommitLayout(result);
            return;
        }

        // ---- 一级 ----
        if (source >= _topSlots.Count || drop.Index < 0 || drop.Index >= _topSlots.Count) return;
        if (source == drop.Index) return;

        if (drop.Mode == DropMode.IntoSlot)
        {
            Logger.Info($"并入: {_topSlots[source].Name} → {_topSlots[drop.Index].Name}");
            CommitLayout(SlotEditor.Merge(_topSlots, source, drop.Index));
            return;
        }

        int target = drop.Mode == DropMode.InsertAfter ? drop.Index + 1 : drop.Index;
        Logger.Info($"重排: {source} → {target} ({_topSlots[source].Name})");
        CommitLayout(SlotEditor.Reorder(_topSlots, source, target));
    }

    private void CommitLayout(IReadOnlyList<ResolvedSlot> slots, int selectIndex = -1)
    {
        _topSlots = slots;
        _openGroup = null;
        _openGroupIndex = -1;
        _vm.Level = SelectorLevel.Top;
        _vm.Breadcrumb = "";
        BuildRows(_topSlots);

        if (selectIndex >= 0 && selectIndex < _vm.Slots.Count)
        {
            _vm.SelectedSlot = _vm.Slots[selectIndex];
            PART_List.ScrollIntoView(_vm.SelectedSlot);
        }
        LayoutChanged?.Invoke(_topSlots);
    }

    // ----------------------------------------------------------
    //  右键菜单：重命名 / 解散分组
    // ----------------------------------------------------------

    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 只有一级才允许改名 / 解散；二级的成员是进程派生出来的，没有独立名字
        if (_vm.Level != SelectorLevel.Top)
        {
            e.Handled = true;
            return;
        }
        var hit = PART_List.InputHitTest(Mouse.GetPosition(PART_List)) as DependencyObject;
        if (hit == null || FindAncestor<ListBoxItem>(hit) is not ListBoxItem item)
        {
            e.Handled = true;        // 空白处不弹菜单
            return;
        }
        if (item.DataContext is SlotViewModel row) _vm.SelectedSlot = row;
    }

    private void OnRenameMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: SlotViewModel row }) return;
        int idx = _vm.Slots.IndexOf(row);
        if (idx < 0 || idx >= _topSlots.Count) return;

        var slot = _topSlots[idx];
        string title = slot.Kind == SlotKind.Group ? "重命名分组" : "重命名程序";
        string? result = RenameDialog.Show(this, title, slot.Name);
        if (result == null) return;                     // 取消

        var slots = _topSlots.ToList();
        slots[idx] = slot.WithName(result);
        Logger.Info($"重命名: {slot.Name} → {(string.IsNullOrWhiteSpace(result) ? "(默认名)" : result)}");
        CommitLayout(slots, idx);
    }

    /// <summary>解散分组：把组内各进程摊成各自独立的槽位，按组内原有顺序排。</summary>
    private void OnDissolveMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: SlotViewModel row }) return;
        if (row.Slot.Kind != SlotKind.Group) return;
        int idx = _vm.Slots.IndexOf(row);
        if (idx < 0 || idx >= _topSlots.Count) return;

        var slots = _topSlots.ToList();
        var group = slots[idx];
        slots.RemoveAt(idx);

        var pieces = new List<ResolvedSlot>();
        foreach (var proc in group.Processes)
        {
            var ws = group.Windows
                .Where(w => string.Equals(w.ProcessName, proc, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (ws.Count == 0) continue;
            pieces.Add(new ResolvedSlot
            {
                Kind = ws.Count > 1 ? SlotKind.Group : SlotKind.Window,
                Name = ws.Count > 1 ? LayoutResolver.AutoName(ws) : ws[0].Title,
                Windows = ws,
                Processes = new[] { proc },
            });
        }
        slots.InsertRange(idx, pieces);

        Logger.Info($"解散分组: {group.Name} → {pieces.Count} 个槽位");
        CommitLayout(slots, idx);
    }

    // ----------------------------------------------------------
    //  Ghost 拖动特效
    // ----------------------------------------------------------

    private void ShowDragGhost()
    {
        if (_dragSourceIndex < 0 || _dragSourceIndex >= _vm.Slots.Count) return;
        var cell = _vm.Slots[_dragSourceIndex];
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
            Width = 240, Height = 34,
            Content = BuildGhostContent(cell),
        };
        var sp = System.Windows.Forms.Cursor.Position;
        var (dipX, dipY) = ScreenPxToDip(sp);
        _ghostWindow.Left = dipX - 120;
        _ghostWindow.Top = dipY - 17;
        _ghostWindow.Show();
        StartDragFollowTimer();
    }

    private void UpdateGhostFromOs()
    {
        if (_ghostWindow == null) return;
        var (dipX, dipY) = ScreenPxToDip(System.Windows.Forms.Cursor.Position);
        _ghostWindow.Left = dipX - 120;
        _ghostWindow.Top = dipY - 17;
    }

    private (double x, double y) ScreenPxToDip(System.Drawing.Point sp)
    {
        if (_ghostWindow == null) return (sp.X, sp.Y);
        var helper = new System.Windows.Interop.WindowInteropHelper(_ghostWindow);
        if (helper.Handle == IntPtr.Zero) return (sp.X, sp.Y);
        var source = System.Windows.Interop.HwndSource.FromHwnd(helper.Handle);
        if (source == null) return (sp.X, sp.Y);
        var dip = source.CompositionTarget.TransformFromDevice.Transform(new System.Windows.Point(sp.X, sp.Y));
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

    private static Border BuildGhostContent(SlotViewModel cell)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 212)),
            BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 10, Opacity = 0.6, ShadowDepth = 3, Color = Colors.Black },
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (cell.Icon != null)
        {
            var icon = new System.Windows.Controls.Image
            {
                Source = cell.Icon,
                Stretch = Stretch.Uniform,
                Width = 20, Height = 20,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var title = new TextBlock
        {
            Text = cell.IsGroup ? $"{cell.Name}  {cell.CountBadge}" : cell.Name,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 221, 221)),
            FontSize = 12,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(title, 1);
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

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
