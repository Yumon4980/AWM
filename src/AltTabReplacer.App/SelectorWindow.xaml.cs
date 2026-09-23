using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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

    /// <summary>网格里显示的槽位数 min(16, 当前层总数)；其余进左侧"未入网格"列表。</summary>
    private int _gridCount;

    /// <summary>一级网格的稀疏排布：每个键位放哪个槽位，null = 空位。拖动后空位会被固化保留。</summary>
    private ResolvedSlot?[] _grid = new ResolvedSlot?[KeyMap.Size];

    /// <summary>选中同步的防重入标志：程序化改 SelectedItem 时不要再回调 Select。</summary>
    private bool _syncingSelection;

    /// <summary>进入组之前一级的高亮项，Esc 回退时恢复。</summary>
    private int _savedTopSelection;

    private readonly Dictionary<IntPtr, BitmapSource?> _iconCache = new();

    /// <summary>已关闭成员的 exe 图标缓存（按路径）。程序没开时网格/预览都用它。</summary>
    private readonly Dictionary<string, BitmapSource?> _exeIconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>建程序组合时反查程序的可执行路径（"未开则启动"要用）。</summary>
    private readonly ProcessPathResolver _pathResolver = new();

    private string? ResolveExePath(WindowInfo w) => _pathResolver.GetPath(w.ProcessId);

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
    /// <summary>
    /// 右键菜单 / 重命名对话框打开期间为 true。
    /// 这两者都会让选择器失焦，但那是我们自己引起的，不该触发"失焦即关闭"——
    /// 否则菜单刚弹出（或对话框刚打开）窗口就把自己关了，表现为"右键点了没反应"。
    /// </summary>
    private bool _suppressAutoClose;
    /// <summary>右键菜单当前指向的行；菜单关掉后置 null。</summary>
    private SlotViewModel? _menuRow;

    /// <summary>布局变了（拖动产生分组/排序），App 负责落盘。</summary>
    public event Action<IReadOnlyList<ResolvedSlot>>? LayoutChanged;

    /// <summary>
    /// 请求 App 暂时放行索引键（16 个键）。
    /// 搜索模式和重命名对话框都要用：那些键平时被全局钩子吞掉，
    /// 不放行的话输入框里打 123qwe 一个字都进不去。
    /// </summary>
    public event Action<bool>? SuspendIndexCaptureChanged;

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
            // 网格列数来自 KeyMap（从 config.json 加载，详见 KeyMapConfig.Load）。
            // XAML 里 UniformGrid 必须留空列数才能让代码后置赋值——直接在 XAML 写 Columns="4"
            // 会让用户配的 5 列 / 6 列失效。UniformGrid 实例的 Columns 由 OnListPanelLoaded
            // 在首次加到可视树时设置。
            ApplyKeyMapHint();
            ApplyPreviewVisibility();     // 缩略图关闭时收起右侧列
            ApplyPrimaryScreenGeometry();
            WindowActivator.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Activate();
            Focus();
        };

        Activated += (_, __) => _everActivated = true;

        Deactivated += (_, __) =>
        {
            if (!_everActivated) return;
            if (_suppressAutoClose) return;     // 菜单/对话框导致的失焦，不是用户切走了
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
    /// 尺寸和位置都从主显示器工作区算：尺寸取固定比例（与分辨率解耦），
    /// 位置按"窗口锚点 = 窗口中心"落在"工作区中心"上。
    /// <br/>
    /// 即 <c>(Left + Width/2, Top + Height/2) == (origin + extent/2)</c>——
    /// 不要用 <c>WindowStartupLocation="CenterScreen"</c>，多显示器下它居中到的不一定是主显示器。
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

        Width = Fit(extent.X, _settings.Layout.WidthRatio, MinSelectorWidth);
        Height = Fit(extent.Y, _settings.Layout.HeightRatio, MinSelectorHeight);

        CenterOnScreen(origin, extent);
    }

    /// <summary>
    /// 窗口锚点 = 窗口中心；让它落在主显示器工作区中心。
    /// 与 Width/Height 联动：调用前先设好 Width/Height。
    /// </summary>
    private void CenterOnScreen(System.Windows.Point origin, System.Windows.Vector extent)
    {
        Left = origin.X + extent.X / 2 - Width / 2;
        Top = origin.Y + extent.Y / 2 - Height / 2;
    }

    private static double Fit(double available, double ratio, double min)
    {
        double v = available * ratio;
        if (v < min) v = min;
        if (v > available) v = available;
        return v;
    }

    /// <summary>
    /// 列表的 ItemsPanel（<see cref="System.Windows.Controls.Primitives.UniformGrid"/>）
    /// 首次被加到可视树时触发，按 <see cref="KeyMap.Cols"/> 把列数推下去。
    /// XAML 里 UniformGrid 故意不写 Columns——硬写 4 会让用户配的 5/6 列失效。
    /// </summary>
    private void OnListPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Primitives.UniformGrid ug)
            ug.Columns = KeyMap.Cols;
    }

    /// <summary>
    /// 把"键位列"动态拼到底部提示里：行与行之间用斜杠分隔，每格之间放一个句点。
    /// 例 4×4 QWERTY → "1234·QWER·ASDF·ZXCV 选网格 · ..."
    /// 例 4×5 例 "1·2·3·4·5/q·w·e·r·t/..."   （分隔符和 layout.json 的二维数组行结构呼应）
    /// </summary>
    private void ApplyKeyMapHint()
    {
        var rows = new List<string>(KeyMap.Rows);
        for (int r = 0; r < KeyMap.Rows; r++)
        {
            var line = new System.Text.StringBuilder(KeyMap.Cols * 2);
            for (int c = 0; c < KeyMap.Cols; c++)
            {
                int idx = r * KeyMap.Cols + c;
                if (idx >= 0 && idx < KeyMap.IndexToLabel.Length)
                {
                    var lbl = KeyMap.LabelOf(idx);
                    if (string.IsNullOrWhiteSpace(lbl) || lbl == "?") continue;     // 空 / 无效键位不显示
                    if (line.Length > 0) line.Append('·');
                    line.Append(lbl);
                }
            }
            if (line.Length > 0) rows.Add(line.ToString());
        }
        string keys = string.Join("/", rows);
        PART_Hint.Text = $"{keys} 选网格 · 方向键移动 · Enter 确认 · / 搜索 · Esc 返回 · 拖到格中=并入，拖到边缘=排序";
    }

    // ----------------------------------------------------------
    //  行构建
    // ----------------------------------------------------------

    private void BuildRows(IReadOnlyList<ResolvedSlot> slots)
    {
        _vm.Slots.Clear();
        _vm.Overflow.Clear();

        // 锁定的槽位固定在自己的键位；未锁定的按列表顺序往前填满其余空位。
        var (grid, overflow) = ArrangeGrid(slots);
        _grid = grid;
        _gridCount = grid.Count(s => s != null);

        // 始终 4×4：空位补不可触发的占位格，键位空间关系不随槽位数变化。
        for (int i = 0; i < KeyMap.Size; i++)
        {
            if (grid[i] != null) _vm.Slots.Add(MakeRow(grid[i]!, KeyMap.LabelOf(i)));
            else _vm.Slots.Add(SlotViewModel.Empty(KeyMap.LabelOf(i)));
        }

        foreach (var s in overflow) _vm.Overflow.Add(MakeRow(s, ""));

        Select(_vm.Slots.FirstOrDefault(r => !r.IsEmpty) ?? _vm.Overflow.FirstOrDefault());
    }

    /// <summary>
    /// 把槽位列表排进 16 键位网格：
    ///   1) 有显式位置（锁定 / 拖动固化过）的槽位先占住自己的键位；
    ///   2) 其余槽位按列表顺序，从前往后填满剩余空位。
    /// 装不下的进溢出列表。空位用 null 表示。
    /// </summary>
    private static (ResolvedSlot?[] grid, List<ResolvedSlot> overflow) ArrangeGrid(IReadOnlyList<ResolvedSlot> slots)
    {
        var grid = new ResolvedSlot?[KeyMap.Size];
        var placed = new bool[slots.Count];

        // 1) 有显式键位（0..15）的槽位先就位
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.Position is int p && p >= 0 && p < KeyMap.Size && grid[p] == null)
            {
                grid[p] = s;
                placed[i] = true;
            }
        }

        // 2) 其余槽位：Position<0 = 强制留在"未入网格"区；Position=null = 自动填第一个空位
        var overflow = new List<ResolvedSlot>();
        int pos = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            if (placed[i]) continue;
            if (slots[i].Position is < 0) { overflow.Add(slots[i]); continue; }
            while (pos < KeyMap.Size && grid[pos] != null) pos++;
            if (pos < KeyMap.Size) grid[pos++] = slots[i];
            else overflow.Add(slots[i]);
        }

        return (grid, overflow);
    }

    private SlotViewModel MakeRow(ResolvedSlot slot, string label)
    {
        BitmapSource? icon = slot.Count > 0 ? IconForMember(slot, 0) : null;
        var icons = (slot.Kind == SlotKind.Group || slot.Kind == SlotKind.Combination)
            ? slot.Windows.Take(4).Select((_, i) => IconForMember(slot, i)).ToList()
            : new List<BitmapSource?>();
        return new SlotViewModel(slot, label, icon, icons);
    }

    /// <summary>
    /// 槽位第 i 个成员的图标。已关闭的成员（Hwnd=0，锁定槽位的"未运行"占位）
    /// 没有活窗口可取，从锁定时记录的可执行路径扒 exe 图标。
    /// </summary>
    private BitmapSource? IconForMember(ResolvedSlot slot, int i)
    {
        var w = slot.Windows[i];
        if (w.Hwnd != IntPtr.Zero) return IconFor(w);
        string? exe = slot.Members is { Count: > 0 } && i < slot.Members.Count
            ? slot.Members[i].ExePath : null;
        return IconForExe(exe ?? slot.LaunchPath);
    }

    /// <summary>
    /// 把程序组展开成二级的行：
    ///   手工组（Children 非 null）→ 每个子项一行（程序 / 程序组合）；
    ///   自动折叠组 / 溢出组 → 每个窗口一行。
    /// </summary>
    private void BuildGroupRows(ResolvedSlot group)
    {
        _vm.Slots.Clear();
        _vm.Overflow.Clear();

        if (group.Children != null)
        {
            for (int i = 0; i < group.Children.Count && i < KeyMap.Size; i++)
                _vm.Slots.Add(MakeRow(group.Children[i], KeyMap.LabelOf(i)));
        }
        else
        {
            for (int i = 0; i < group.Windows.Count && i < KeyMap.Size; i++)
            {
                var w = group.Windows[i];
                // 成员自定义名优先（用户二级右键"重命名程序"改过的），否则用窗口标题。
                string? custom = group.Members is { Count: > 0 } && i < group.Members.Count
                    ? group.Members[i].DisplayName
                    : null;
                // 自动折叠组的成员锁定存放在 MemberSpec.Locked 上（Children==null 时无法挂到子槽位）。
                bool locked = group.Members is { Count: > 0 } && i < group.Members.Count
                    && group.Members[i].Locked;
                var leaf = new ResolvedSlot
                {
                    Kind = SlotKind.Window,
                    Name = string.IsNullOrWhiteSpace(custom) ? w.Title : custom!,
                    Windows = new[] { w },
                    Processes = new[] { w.ProcessName },
                    Members = new[] { new MemberSpec(w.ProcessName, w.Title)
                    {
                        DisplayName = custom,
                        ExePath = group.Members is { Count: > 0 } && i < group.Members.Count
                            ? group.Members[i].ExePath : null,
                        Hwnd = (long)w.Hwnd,
                        Locked = locked,
                    } },
                    Locked = locked,
                };
                _vm.Slots.Add(MakeRow(leaf, KeyMap.LabelOf(i)));
            }
        }

        _gridCount = _vm.Slots.Count;
        for (int i = _gridCount; i < KeyMap.Size; i++)
            _vm.Slots.Add(SlotViewModel.Empty(KeyMap.LabelOf(i)));
        Select(_vm.Slots.FirstOrDefault(r => !r.IsEmpty));
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

    /// <summary>从可执行文件路径提取图标（未运行的锁定槽位用）。文件不存在 / 无图标返回 null。</summary>
    private BitmapSource? IconForExe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (_exeIconCache.TryGetValue(exePath, out var cached)) return cached;

        BitmapSource? icon = null;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (ico != null) icon = HIconToBitmapSource(ico.Handle);
        }
        catch (Exception ex)
        {
            Logger.Warn($"提取程序图标失败 ({exePath}): {ex.Message}");
        }
        _exeIconCache[exePath] = icon;
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
        if (_vm.Level == SelectorLevel.Search) ApplySearchFilter();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (PART_List.SelectedItem is SlotViewModel s) Select(s);
    }

    private void OnOverflowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (PART_Overflow.SelectedItem is SlotViewModel s) Select(s);
    }

    /// <summary>统一的选中入口：更新 SelectedSlot、两个列表的选中态与滚动，并刷新预览。</summary>
    private void Select(SlotViewModel? slot)
    {
        if (slot != null && ReferenceEquals(slot, _vm.SelectedSlot)) { UpdatePreview(); return; }

        _syncingSelection = true;
        try
        {
            _vm.SelectedSlot = slot;
            PART_List.SelectedItem = slot != null && _vm.Slots.Contains(slot) ? slot : null;
            PART_Overflow.SelectedItem = slot != null && _vm.Overflow.Contains(slot) ? slot : null;
            if (slot != null)
            {
                if (_vm.Slots.Contains(slot)) PART_List.ScrollIntoView(slot);
                else if (_vm.Overflow.Contains(slot)) PART_Overflow.ScrollIntoView(slot);
            }
        }
        finally { _syncingSelection = false; }

        UpdatePreview();
    }

    /// <summary>按"完整列表"索引选中（网格 0..15，溢出 16+）。空占位格会被忽略。</summary>
    private void SelectByFullIndex(int fullIndex)
    {
        var row = CurrentRowAt(fullIndex);
        if (row != null) Select(row);
    }

    /// <summary>取"完整列表"索引对应的行：网格用 0..15，溢出用 16+。空占位格返回 null。</summary>
    private SlotViewModel? CurrentRowAt(int fullIndex)
    {
        if (fullIndex < 0) return null;
        if (fullIndex < KeyMap.Size)
        {
            if (fullIndex >= _vm.Slots.Count) return null;
            var r = _vm.Slots[fullIndex];
            return r.IsEmpty ? null : r;
        }
        int oi = fullIndex - KeyMap.Size;
        return oi < _vm.Overflow.Count ? _vm.Overflow[oi] : null;
    }

    /// <summary>行在"完整列表"里的位置（网格键位 0..15，溢出 16+）；找不到返回 -1。</summary>
    private int CurrentIndexOf(SlotViewModel row)
    {
        int gi = _vm.Slots.IndexOf(row);
        if (gi >= 0 && !row.IsEmpty) return gi;
        int oi = _vm.Overflow.IndexOf(row);
        return oi >= 0 ? KeyMap.Size + oi : -1;
    }

    /// <summary>键位 / 溢出位置 → 一级槽位列表 _topSlots 里的索引；空位返回 -1。</summary>
    private int TopIndexOfFull(int full)
    {
        var row = CurrentRowAt(full);
        return row == null ? -1 : TopIndexOf(row.Slot);
    }

    /// <summary>键位 / 溢出位置 → 插入用的 _topSlots 索引（空位取其后第一个槽位的索引，没有则末尾）。</summary>
    private int TopInsertIndexForFull(int full)
    {
        var row = CurrentRowAt(full);
        if (row != null) return TopIndexOf(row.Slot);

        if (full < KeyMap.Size)
        {
            for (int i = full + 1; i < KeyMap.Size; i++)
                if (!_vm.Slots[i].IsEmpty) return TopIndexOf(_vm.Slots[i].Slot);
            if (_vm.Overflow.Count > 0) return TopIndexOf(_vm.Overflow[0].Slot);
        }
        return _topSlots.Count;
    }

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

        string title = _vm.SelectedSlot!.Slot.Kind == SlotKind.Group
            ? $"{_vm.SelectedSlot.Slot.Name} — {rep.Title}"
            : rep.Title;

        // 锁定槽位的"未运行"占位（Hwnd=0）：截不到内容，显示 exe 图标 + 重启提示
        if (rep.Hwnd == IntPtr.Zero)
        {
            PART_PreviewTitle.Text = title + "（未运行）";
            PART_PreviewImage.Source = ComposeIconPreview(
                IconForMember(_vm.SelectedSlot.Slot, 0), "程序未运行 — 按 Enter 重新启动");
            PART_PreviewPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        // 最小化窗口截不到内容：PrintWindow 只返回黑图、GetClientRect 还会给出 0 尺寸。
        // 与其让预览区一片黑，不如退回"大图标 + 已最小化"的提示图。
        if (rep.IsMinimized)
        {
            PART_PreviewTitle.Text = title + "（已最小化）";
            PART_PreviewImage.Source = ComposeIconPreview(IconFor(rep), "窗口已最小化");
            PART_PreviewPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        PART_PreviewTitle.Text = title;

        // 缩略图关闭时直接走大图标预览：跳过截图、免去 PrintWindow 的几百毫秒开销，
        // 也能避开截图相关的崩溃（GPU 独占渲染、安全软件拦截等）。
        if (!_settings.Behavior.ShowThumbnails)
        {
            PART_PreviewImage.Source = ComposeIconPreview(IconFor(rep), "缩略图已关闭（托盘菜单 → 缩略图）");
            PART_PreviewPlaceholder.Visibility = Visibility.Collapsed;
            return;
        }

        BitmapSource? bmp = null;
        try { bmp = _capture.Capture(rep.Hwnd, 0, 0); } catch { /* 下面统一兜底 */ }

        if (bmp != null)
        {
            PART_PreviewImage.Source = bmp;
        }
        else
        {
            // 截不到（句柄失效 / 尺寸为 0 等）也退回大图标，别留一片空白或黑。
            PART_PreviewImage.Source = ComposeIconPreview(IconFor(rep), "无法预览此窗口");
        }
        PART_PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 外部切换了缩略图开关后调用这个让预览立即刷新（不重启选择器）。
    /// 由 <see cref="App.OnToggleThumbnails"/> 调用。
    /// </summary>
    public void RefreshPreview()
    {
        ApplyPreviewVisibility();
        if (!_everActivated) return;
        UpdatePreview();
    }

    /// <summary>
    /// 缩略图关闭时收起右侧预览列（宽度 0 + 隐藏 Border），
    /// 同时禁用 ListBox 滚动条并把窗口高度撑到刚好装下 R×C 个单元格。
    /// 缩略图开启时恢复 64/36 的双列布局和屏幕比例的高度。
    /// </summary>
    private void ApplyPreviewVisibility()
    {
        bool show = _settings.Behavior.ShowThumbnails;
        PART_RightCol.Width = show ? new GridLength(0.36, GridUnitType.Star) : new GridLength(0);
        PART_PreviewPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // 缩略图关闭时禁止 ListBox 滚动——下面 FitHeightToGrid 会把窗口高度撑开
        PART_List.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
            show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled);

        if (show)
        {
            // 恢复屏幕比例高度
            ApplyPrimaryScreenGeometry();
        }
        else
        {
            // 推迟到 Loaded 优先级：那时 PART_List.ActualWidth 已经是新的（全列宽），
            // 否则算 cellSize 时还是旧宽度，撑出来的高度偏小
            Dispatcher.BeginInvoke(new Action(FitHeightToGrid), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 把窗口高度撑到刚好装下所有键位单元格（不出现滚动条）。
    /// 算法：临时 Measure 一次 PART_Root，DesiredSize.Height 就是最小内容高度，
    /// 再加上 PART_Root 内 Grid.Margin + chrome 缓冲。
    /// <br/>
    /// 高度变了之后让窗口重新垂直居中：锚点（窗口中心）必须落在工作区中心。
    /// </summary>
    private void FitHeightToGrid()
    {
        if (!IsLoaded || ActualWidth <= 0) return;

        // 用当前窗口宽度测一次 PART_Root（不影响 visual tree）
        PART_Root.Measure(new System.Windows.Size(ActualWidth, double.PositiveInfinity));
        double desired = PART_Root.DesiredSize.Height;

        // PART_Root 内 Grid.Margin="12"（上下各 12）
        // WindowStyle=None + AllowsTransparency=True，Window 没有标题栏 / 边框 chrome
        const double gridMargin = 24;

        double target = Math.Max(MinSelectorHeight, desired + gridMargin);
        if (Math.Abs(Height - target) <= 0.5) return;

        Height = target;

        // 高度变了，重做一次定位。水平方向居中已经在 ApplyPrimaryScreenGeometry 时设过了；
        // 这里只重算 Top，让窗口中心再次落在屏幕中心。
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var wa = screen.WorkingArea;
        var m = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(this)!)
            .CompositionTarget!.TransformFromDevice;
        var origin = m.Transform(new System.Windows.Point(wa.Left, wa.Top));
        var extent = m.Transform(new System.Windows.Vector(wa.Width, wa.Height));
        CenterOnScreen(origin, extent);
    }

    /// <summary>合成一张占位预览图：深色底 + 居中大图标 + 一行提示文字。</summary>
    private static BitmapSource ComposeIconPreview(BitmapSource? icon, string message)
    {
        const int W = 640, H = 480, IconSize = 128;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0x10, 0x10)),
                null, new Rect(0, 0, W, H));
            if (icon != null)
                dc.DrawImage(icon, new Rect((W - IconSize) / 2.0, (H - IconSize) / 2.0 - 24, IconSize, IconSize));

            var text = new FormattedText(
                message,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                18,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                96);
            dc.DrawText(text, new System.Windows.Point((W - text.Width) / 2, (H + IconSize) / 2.0));
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
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

        // 右键菜单开着时，Esc 只关菜单，不往上一级退
        if (PART_Menu.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape)
            {
                e.Handled = true;
                HideRowMenu();
                return;
            }
        }

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
                MoveGrid(0, 1);
                return;

            case Key.Up:
                e.Handled = true;
                MoveGrid(0, -1);
                return;

            case Key.Left:
                e.Handled = true;
                MoveGrid(-1, 0);
                return;

            case Key.Right:
                e.Handled = true;
                MoveGrid(1, 0);
                return;

            case Key.Tab:
                e.Handled = true;
                MoveLinear((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
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
    /// 一级：程序组 → 进入二级；程序组合 → 打开全部成员；程序 → 直接切
    /// 二级：程序组合 → 打开全部成员；程序 → 直接切
    /// </summary>
    public void HandleVk(int vk)
    {
        if (_vm.Level == SelectorLevel.Search) return;
        // Ctrl 按着时不响应索引键：Ctrl+1/2/3/4 这类快捷键让给系统 / 前台程序
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        if (KeyMap.ToIndex(vk) is not int index) return;

        var rows = _vm.Slots;                    // 只有 4×4 网格里的行有索引键
        if (index >= rows.Count) return;        // 没有映射的键：忽略，不做任何事
        if (rows[index].IsEmpty) return;        // 空占位格不响应

        ActivateRow(rows[index]);
    }

    /// <summary>确认某个行：程序组进二级，程序组合打开全部，程序切过去。</summary>
    private void ActivateRow(SlotViewModel row)
    {
        if (_vm.Level == SelectorLevel.Top && row.Slot.Kind == SlotKind.Group)
        {
            EnterGroup(row);
            return;
        }
        if (row.Slot.Kind == SlotKind.Combination)
        {
            OpenCombinationAndClose(row.Slot);
            return;
        }
        ActivateAndClose(row.Slot);
    }

    /// <summary>进入某个组（二级）。</summary>
    private void EnterGroup(SlotViewModel row)
    {
        var group = row.Slot;
        int topIdx = TopIndexOf(group);   // SlotEditor 操作要用 _topSlots 索引
        if (topIdx < 0) return;

        int pos = CurrentIndexOf(row);           // 键位/溢出位置，回退时用它恢复高亮
        _savedTopSelection = pos;
        _openGroup = group;
        _openGroupIndex = topIdx;
        _vm.Level = SelectorLevel.InGroup;
        _vm.Breadcrumb = pos >= 0 && pos < KeyMap.Size
            ? $"{KeyMap.LabelOf(pos)} › {group.Name}"
            : group.Name;
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
                SelectByFullIndex(_savedTopSelection);
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
        _vm.SearchText = "";
        PART_SearchBox.Text = "";
        ApplySearchFilter();
        SuspendIndexCaptureChanged?.Invoke(true);        // 让钩子放行索引键，否则打不进字
        Dispatcher.BeginInvoke(() => Keyboard.Focus(PART_SearchBox));
    }

    /// <summary>按当前搜索词重建网格 + 溢出列表（扁平列出所有窗口，跨组搜索才有意义）。</summary>
    private void ApplySearchFilter()
    {
        _vm.Slots.Clear();
        _vm.Overflow.Clear();

        string text = _vm.SearchText;
        var matches = new List<WindowInfo>();
        foreach (var w in _rawWindows)
        {
            if (string.IsNullOrEmpty(text)
                || w.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(text, StringComparison.OrdinalIgnoreCase))
                matches.Add(w);
        }

        _gridCount = Math.Min(KeyMap.Size, matches.Count);
        for (int i = 0; i < _gridCount; i++)
            _vm.Slots.Add(MakeRow(SearchLeaf(matches[i]), KeyMap.LabelOf(i)));

        for (int i = _gridCount; i < KeyMap.Size; i++)
            _vm.Slots.Add(SlotViewModel.Empty(KeyMap.LabelOf(i)));

        for (int i = KeyMap.Size; i < matches.Count; i++)
            _vm.Overflow.Add(MakeRow(SearchLeaf(matches[i]), ""));

        Select(_vm.Slots.FirstOrDefault(r => !r.IsEmpty) ?? _vm.Overflow.FirstOrDefault());
    }

    private static ResolvedSlot SearchLeaf(WindowInfo w) => new()
    {
        Kind = SlotKind.Window,
        Name = w.Title,
        Windows = new[] { w },
        Processes = new[] { w.ProcessName },
    };

    private void ExitSearch()
    {
        SuspendIndexCaptureChanged?.Invoke(false);
        _vm.SearchText = "";
        PART_SearchBox.Text = "";
        _vm.Level = SelectorLevel.Top;
        _vm.Breadcrumb = "";
        BuildRows(_topSlots);
        Focus();
    }

    /// <summary>Tab 用的线性移动：在"网格非空位 + 溢出"里循环。</summary>
    private void MoveLinear(int delta)
    {
        var order = NonEmptyOrder();
        if (order.Count == 0) return;

        int cur = _vm.SelectedSlot != null ? order.IndexOf(_vm.SelectedSlot) : -1;
        int next = cur < 0
            ? (delta > 0 ? 0 : order.Count - 1)
            : ((cur + delta) % order.Count + order.Count) % order.Count;

        Select(order[next]);
    }

    /// <summary>方向键：网格内按 2D 走（和物理键位一致，跳过空位），上下可进出左侧溢出列表。</summary>
    private void MoveGrid(int dx, int dy)
    {
        if (_vm.Slots.All(r => r.IsEmpty) && _vm.Overflow.Count == 0) return;

        int cur = _vm.SelectedSlot != null ? CurrentIndexOf(_vm.SelectedSlot) : -1;
        if (cur < 0) { var first = FirstNonEmpty(); if (first != null) Select(first); return; }

        if (cur < KeyMap.Size)
        {
            int row = cur / KeyMap.Cols;
            int col = cur % KeyMap.Cols;

            if (dx != 0)
            {
                for (int c = col + dx; c >= 0 && c < KeyMap.Cols; c += dx)
                {
                    int ni = row * KeyMap.Cols + c;
                    if (!_vm.Slots[ni].IsEmpty) { Select(_vm.Slots[ni]); return; }
                }
                return;
            }

            if (dy != 0)
            {
                for (int r = row + dy; r >= 0 && r < KeyMap.Rows; r += dy)
                {
                    int ni = r * KeyMap.Cols + col;
                    if (ni < _vm.Slots.Count && !_vm.Slots[ni].IsEmpty) { Select(_vm.Slots[ni]); return; }
                }
                if (dy > 0 && _vm.Overflow.Count > 0) Select(_vm.Overflow[0]);   // 往下进溢出列表
                return;
            }
            return;
        }

        // 溢出列表内：上下移动，往上越界回到网格最后一个非空位
        int oi = cur - KeyMap.Size;
        if (dy != 0)
        {
            int noi = oi + dy;
            if (noi >= 0 && noi < _vm.Overflow.Count) { Select(_vm.Overflow[noi]); return; }
            if (noi < 0)
            {
                for (int i = _vm.Slots.Count - 1; i >= 0; i--)
                    if (!_vm.Slots[i].IsEmpty) { Select(_vm.Slots[i]); return; }
            }
        }
    }

    private List<SlotViewModel> NonEmptyOrder()
    {
        var order = new List<SlotViewModel>();
        foreach (var r in _vm.Slots) if (!r.IsEmpty) order.Add(r);
        order.AddRange(_vm.Overflow);
        return order;
    }

    private SlotViewModel? FirstNonEmpty()
    {
        foreach (var r in _vm.Slots) if (!r.IsEmpty) return r;
        return _vm.Overflow.FirstOrDefault();
    }

    // ----------------------------------------------------------
    //  激活 / 关闭
    // ----------------------------------------------------------

    /// <summary>确认当前选中项：程序组进去，程序组合打开全部，程序切过去。</summary>
    public void ConfirmAndClose()
    {
        var sel = _vm.SelectedSlot;
        if (sel == null) { Cancel(); return; }
        ActivateRow(sel);
    }

    private void ActivateAndClose(ResolvedSlot slot)
    {
        var target = slot.SingleWindow;

        // 锁定槽位的窗口已全部关闭（Hwnd=0 的"未运行"占位）：用记录的可执行路径重新启动
        if (target != null && target.Hwnd == IntPtr.Zero)
        {
            if (_closing) return;
            _closing = true;
            Logger.Info($"重新启动已关闭的程序: {slot.Name}");
            try { _activator.RelaunchClosed(slot); }
            catch (Exception ex) { Logger.Error($"重新启动失败: {ex.Message}"); }
            Close();
            return;
        }

        if (target == null) { Cancel(); return; }
        if (_closing) return;

        // 先置位再激活：切到目标窗口会让本窗口 Deactivated，别让它抢在前面重入 Close
        _closing = true;
        try { _activator.Activate(target); }
        catch (Exception ex) { Logger.Error($"激活失败: {ex.Message}"); }
        Close();
    }

    /// <summary>打开程序组合（已开激活、未开启动），然后关闭选择器。</summary>
    private void OpenCombinationAndClose(ResolvedSlot combo)
    {
        if (_closing) return;
        _closing = true;
        try { _activator.OpenCombination(combo); }
        catch (Exception ex) { Logger.Error($"打开程序组合失败: {ex.Message}"); }
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
        // 右键菜单开着时，点别处先关掉菜单，并吃掉这次点击（避免顺带触发拖动/切窗）。
        if (PART_Menu.Visibility == Visibility.Visible)
        {
            HideRowMenu();
            e.Handled = true;
            return;
        }

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
        else if (PART_Overflow.Visibility == Visibility.Visible)
        {
            var ohit = PART_Overflow.InputHitTest(e.GetPosition(PART_Overflow)) as DependencyObject;
            if (ohit != null && FindAncestor<ListBoxItem>(ohit) is ListBoxItem oitem)
                _dragSourceIndex = KeyMap.Size + PART_Overflow.ItemContainerGenerator.IndexFromContainer(oitem);
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
        if (HitRow(PART_List, e) is SlotViewModel cell && cell != _vm.SelectedSlot) { Select(cell); return; }
        if (PART_Overflow.Visibility == Visibility.Visible
            && HitRow(PART_Overflow, e) is SlotViewModel o && o != _vm.SelectedSlot)
            Select(o);
    }

    private static SlotViewModel? HitRow(System.Windows.Controls.ListBox list, System.Windows.Input.MouseEventArgs e)
    {
        if (!list.IsVisible) return null;
        var hit = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        return hit == null ? null : FindAncestor<ListBoxItem>(hit)?.DataContext as SlotViewModel;
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
    ///   中 50% → 并入本行：目标是程序组 → 加入组；目标是程序/程序组合 → 建/扩程序组合
    ///   下 25% → 插到本行之后（排序）
    ///   二级拖到面包屑 → 移出该组
    /// Shift 强制排序、Ctrl 强制并入，作为高级用户的逃生通道。
    /// </summary>
    private DropTarget HitTestDrop(System.Drawing.Point screenPos)
    {
        // 二级：面包屑是"移出该组"的落点
        if (_vm.Level == SelectorLevel.InGroup && IsOverElement(PART_Breadcrumb, screenPos))
        {
            return new DropTarget(-1, DropMode.OutOfGroup);
        }

        // 搜索模式：禁止拖动
        if (_vm.Level == SelectorLevel.Search) return DropTarget.None;

        bool forceOrder = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool forceGroup = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // 网格：左右两翼=排序，中间=并入
        for (int i = 0; i < _vm.Slots.Count; i++)
        {
            if (PART_List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            if (item.ActualWidth <= 0 || item.ActualHeight <= 0) continue;

            var tl = item.PointToScreen(new System.Windows.Point(0, 0));
            var br = item.PointToScreen(new System.Windows.Point(item.ActualWidth, item.ActualHeight));
            if (screenPos.X < tl.X || screenPos.X >= br.X || screenPos.Y < tl.Y || screenPos.Y >= br.Y) continue;

            // 空占位格：不能并入，但和真实槽位一样按左右半边判断排序落点
            //   （左半边 = 插到它前面，右半边 = 插到它后面）
            if (_vm.Slots[i].IsEmpty)
            {
                double relE = (screenPos.X - tl.X) / (br.X - tl.X);
                return new DropTarget(i, relE < 0.5 ? DropMode.InsertBefore : DropMode.InsertAfter);
            }

            double relX = (screenPos.X - tl.X) / (br.X - tl.X);
            if (forceGroup) return new DropTarget(i, DropMode.IntoSlot);
            if (forceOrder) return new DropTarget(i, relX < 0.5 ? DropMode.InsertBefore : DropMode.InsertAfter);
            if (relX < 0.25) return new DropTarget(i, DropMode.InsertBefore);
            if (relX > 0.75) return new DropTarget(i, DropMode.InsertAfter);
            return new DropTarget(i, DropMode.IntoSlot);
        }

        // 溢出列表：上下两翼=排序，中间=并入
        for (int i = 0; i < _vm.Overflow.Count; i++)
        {
            if (PART_Overflow.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item) continue;
            if (item.ActualHeight <= 0) continue;

            var tl = item.PointToScreen(new System.Windows.Point(0, 0));
            var br = item.PointToScreen(new System.Windows.Point(item.ActualWidth, item.ActualHeight));
            if (screenPos.Y < tl.Y || screenPos.Y >= br.Y) continue;

            double rel = (screenPos.Y - tl.Y) / (br.Y - tl.Y);
            int full = KeyMap.Size + i;
            if (forceGroup) return new DropTarget(full, DropMode.IntoSlot);
            if (forceOrder) return new DropTarget(full, rel < 0.5 ? DropMode.InsertBefore : DropMode.InsertAfter);
            if (rel < 0.25) return new DropTarget(full, DropMode.InsertBefore);
            if (rel > 0.75) return new DropTarget(full, DropMode.InsertAfter);
            return new DropTarget(full, DropMode.IntoSlot);
        }

        // 落在"未入网格"区（哪怕为空）→ 追加到未入网格末尾
        if (IsOverElement(PART_OverflowHost, screenPos))
            return new DropTarget(KeyMap.Size + _vm.Overflow.Count, DropMode.InsertAfter);

        // 落在网格空白处 → 追加到网格末尾
        int last = LastNonEmptyFullIndex();
        if (last >= 0 && IsOverElement(PART_List, screenPos))
            return new DropTarget(last, DropMode.InsertAfter);

        return DropTarget.None;
    }

    private int LastNonEmptyFullIndex()
    {
        if (_vm.Overflow.Count > 0) return KeyMap.Size + _vm.Overflow.Count - 1;
        for (int i = _vm.Slots.Count - 1; i >= 0; i--)
            if (!_vm.Slots[i].IsEmpty) return i;
        return -1;
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
        foreach (var s in _vm.Overflow) s.IsDropTarget = false;
        _vm.BreadcrumbIsDropTarget = drop.Mode == DropMode.OutOfGroup;
        PART_InsertLine.Visibility = Visibility.Collapsed;

        if (drop.Mode == DropMode.IntoSlot && drop.Index >= 0)
        {
            // 自己并入自己没有意义
            if (drop.Index != _dragSourceIndex) SetDropTarget(drop.Index, true);
            return;
        }

        if (drop.Mode is DropMode.InsertBefore or DropMode.InsertAfter && drop.Index >= 0)
        {
            if (!TryGetContainer(drop.Index, out var item)) return;
            // 转到 PART_DropHost（溢出列与网格的共同父级，DropLayer 也在它里面）
            var p = item.TransformToAncestor(PART_DropHost).Transform(new System.Windows.Point(0, 0));

            if (drop.Index < KeyMap.Size)
            {
                // 网格里"前后"是水平方向 → 竖线
                double x = drop.Mode == DropMode.InsertBefore ? p.X : p.X + item.ActualWidth;
                PART_InsertLine.Width = 2;
                PART_InsertLine.Height = item.ActualHeight;
                Canvas.SetLeft(PART_InsertLine, x - 1);
                Canvas.SetTop(PART_InsertLine, p.Y);
            }
            else
            {
                // 溢出列表里"前后"是垂直方向 → 横线
                double y = drop.Mode == DropMode.InsertBefore ? p.Y : p.Y + item.ActualHeight;
                PART_InsertLine.Width = item.ActualWidth;
                PART_InsertLine.Height = 2;
                Canvas.SetLeft(PART_InsertLine, p.X);
                Canvas.SetTop(PART_InsertLine, y - 1);
            }
            PART_InsertLine.Visibility = Visibility.Visible;
        }
    }

    /// <summary>取完整列表索引对应的容器（网格或溢出）。</summary>
    private bool TryGetContainer(int fullIndex, out ListBoxItem item)
    {
        item = null!;
        if (fullIndex < 0) return false;
        if (fullIndex < KeyMap.Size)
        {
            if (fullIndex < _vm.Slots.Count
                && PART_List.ItemContainerGenerator.ContainerFromIndex(fullIndex) is ListBoxItem it)
            {
                item = it;
                return true;
            }
            return false;
        }
        int oi = fullIndex - KeyMap.Size;
        if (oi >= 0 && oi < _vm.Overflow.Count
            && PART_Overflow.ItemContainerGenerator.ContainerFromIndex(oi) is ListBoxItem oit)
        {
            item = oit;
            return true;
        }
        return false;
    }

    private void SetDropTarget(int fullIndex, bool value)
    {
        if (fullIndex < KeyMap.Size)
        {
            if (fullIndex >= 0 && fullIndex < _vm.Slots.Count) _vm.Slots[fullIndex].IsDropTarget = value;
        }
        else
        {
            int oi = fullIndex - KeyMap.Size;
            if (oi >= 0 && oi < _vm.Overflow.Count) _vm.Overflow[oi].IsDropTarget = value;
        }
    }

    private void ClearDropFeedback()
    {
        foreach (var s in _vm.Slots) s.IsDropTarget = false;
        foreach (var s in _vm.Overflow) s.IsDropTarget = false;
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
        // ---- 二级：拖到面包屑 = 把这个成员移出该组 ----
        if (drop.Mode == DropMode.OutOfGroup)
        {
            if (_openGroup == null || _openGroupIndex < 0) return;
            // 锁定的成员不能被移出，与一级"锁定槽位不能拖动"对齐
            if (source >= 0 && source < _vm.Slots.Count && _vm.Slots[source].Slot.Locked)
            {
                Logger.Info($"组内成员已锁定，不能移出: {_vm.Slots[source].Name}");
                return;
            }

            List<ResolvedSlot>? result;
            if (_openGroup.Children != null)
            {
                result = SlotEditor.MoveChildOutOfGroup(_topSlots, _openGroupIndex, source);
                if (result == null) { Logger.Info("移出失败：只剩一个成员"); return; }
                Logger.Info("移出子项");
            }
            else
            {
                if (source >= _openGroup.Windows.Count) return;
                var moved = _openGroup.Windows[source];
                result = SlotEditor.MoveWindowOutOfGroup(_topSlots, _openGroupIndex, moved);
                if (result == null) { Logger.Info($"移出失败: {moved.Title} 是该组唯一窗口"); return; }
                Logger.Info($"移出组: {moved.Title} ({moved.ProcessName})");
            }
            // 留在二级：组还在，只是少了一个成员
            CommitGroupReorder(result, _openGroupIndex, -1);
            return;
        }

        // ---- 二级：组内排序 / 组内并成程序组合 ----
        if (_vm.Level == SelectorLevel.InGroup)
        {
            if (_openGroup == null || _openGroupIndex < 0) return;
            if (source < 0 || source >= _gridCount) return;
            if (_gridCount <= 1) return;
            // 锁定的成员不能作为拖动源；目标若被锁定也不能被并入（与一级行为对齐）
            if (_vm.Slots[source].Slot.Locked)
            {
                Logger.Info($"组内成员已锁定，不能拖动: {_vm.Slots[source].Name}");
                return;
            }

            if (drop.Mode == DropMode.IntoSlot)
            {
                if (drop.Index == source) return;
                if (drop.Index >= 0 && drop.Index < _vm.Slots.Count && _vm.Slots[drop.Index].Slot.Locked)
                {
                    Logger.Info($"锁定的成员不能被并入: {_vm.Slots[drop.Index].Name}");
                    return;
                }
                var combined = SlotEditor.CombineWithinGroup(
                    _topSlots, _openGroupIndex, source, drop.Index, ResolveExePath);
                if (combined == null)
                {
                    Logger.Info("程序组合最多 4 个程序，未加入");
                    return;
                }
                Logger.Info($"组内并成程序组合: {source} → {drop.Index}");
                CommitGroupReorder(combined, _openGroupIndex, Math.Min(drop.Index, _gridCount - 1));
                return;
            }

            int memberTarget = drop.Mode == DropMode.InsertAfter ? drop.Index + 1 : drop.Index;
            Logger.Info($"组内排序: {source} → {memberTarget}");
            var result = SlotEditor.ReorderWithinGroup(_topSlots, _openGroupIndex, source, memberTarget);
            if (result == null) return;
            CommitGroupReorder(result, _openGroupIndex, memberTarget);
            return;
        }

        // ---- 一级：键位/溢出位置先换算成 _topSlots 索引 ----
        int src = TopIndexOfFull(source);
        if (src < 0) return;
        if (_topSlots[src].Locked) { Logger.Info($"槽位已锁定，不能拖动: {_topSlots[src].Name}"); return; }

        // 并入（网格 / 未入网格逻辑相同）：目标是程序组 → 加进组；否则 → 建/扩程序组合
        if (drop.Mode == DropMode.IntoSlot)
        {
            int dst = TopIndexOfFull(drop.Index);
            if (dst < 0 || dst == src) return;       // 空位不能并入
            var target = _topSlots[dst];
            int targetPin = GridPinOf(target);       // 目标在网格里的键位；未入网格 = -1

            // 程序组不能合成组合：合并后程序组本身的"包含关系"就没了
            if (_topSlots[src].Kind == SlotKind.Group)
            {
                Logger.Info($"程序组不能合成组合: {_topSlots[src].Name}");
                return;
            }

            // 目标是程序组 → 把 source 加进组（**程序组位置不变**）
            if (target.Kind == SlotKind.Group)
            {
                var added = SlotEditor.AddToGroup(_topSlots, dst, src);
                if (added == null) { Logger.Info("无法加入程序组"); return; }
                int gi = dst > src ? dst - 1 : dst;      // 源被移除后组左移一位
                if (gi >= 0 && gi < added.Count) added[gi].Position = targetPin;   // 钉住程序组的位置
                Logger.Info($"加入程序组: {_topSlots[src].Name} → {target.Name}");
                CommitLayout(added, Math.Min(gi, added.Count - 1), compact: false);
                return;
            }

            // 目标是锁定的程序 / 程序组合：合并后它就消失了，不能动
            if (target.Locked)
            {
                Logger.Info($"锁定的程序不能被合并进组合: {target.Name}");
                return;
            }

            // 目标是程序 / 程序组合 → 建/扩程序组合
            var combo = SlotEditor.CreateCombination(_topSlots, src, dst, ResolveExePath);
            if (combo == null)
            {
                Logger.Info("程序组合最多 4 个程序，未加入");
                return;
            }
            // 结果组合继承目标的位置（网格键位 / 未入网格）
            int comboIdx = dst > src ? dst - 1 : dst;      // 源被移除后目标左移一位
            if (comboIdx >= 0 && comboIdx < combo.Count) combo[comboIdx].Position = targetPin;
            Logger.Info($"程序组合: {_topSlots[src].Name} + {target.Name}");
            CommitLayout(combo, Math.Min(comboIdx, combo.Count - 1), compact: false);
            return;
        }

        // 网格 → 未入网格：把该槽位移出网格（源键位留空，不动其它格）
        if (source < KeyMap.Size && drop.Index >= KeyMap.Size)
        {
            MoveGridSlotToOverflow(source);
            return;
        }

        // 未入网格 → 拖回网格：插到目标键位，后面的内容往后挤
        if (source >= KeyMap.Size && drop.Index < KeyMap.Size)
        {
            int target = drop.Index;
            if (drop.Mode == DropMode.InsertAfter) target = drop.Index + 1;
            target = Math.Clamp(target, 0, KeyMap.Size - 1);
            Logger.Info($"未入网格 → 网格: {_topSlots[src].Name} @ {KeyMap.LabelOf(target)}");
            InsertOverflowIntoGrid(_topSlots[src], target);
            return;
        }

        // 网格内"插入"（拖到 P 的右半边插到 P+1，左半边插到 P，后面的内容往后挤）
        if (source < KeyMap.Size && drop.Index < KeyMap.Size)
        {
            int target = drop.Index;
            if (drop.Mode == DropMode.InsertAfter) target = drop.Index + 1;
            target = Math.Clamp(target, 0, KeyMap.Size - 1);
            if (target != source) InsertInGrid(source, target);
            return;
        }

        // 未入网格内排序：只动未入网格列表，网格位置不动
        int t = TopInsertIndexForFull(drop.Index);
        if (drop.Mode == DropMode.InsertAfter) t++;
        t = Math.Clamp(t, 0, _topSlots.Count);
        Logger.Info($"重排: {src} → {t} ({_topSlots[src].Name})");
        CommitLayout(SlotEditor.Reorder(_topSlots, src, t), compact: false);
    }

    /// <summary>槽位当前的网格键位（0..15）；在"未入网格"区返回 -1。</summary>
    private int GridPinOf(ResolvedSlot slot)
    {
        for (int i = 0; i < KeyMap.Size; i++)
            if (ReferenceEquals(_grid[i], slot)) return i;
        return -1;
    }

    /// <summary>
    /// 网格内插入：把 from 键位的槽位插到 to 键位，后面的内容依次往右挤（"对方往后挤"）。
    ///   - 空位不能作为拖动源（没内容可拖）；
    ///   - 锁定槽位不能作为源，也不会被挤动（原地不动，后面的内容从它上方跨过去）；
    ///   - 源位置留下一个空位。
    /// 例：A B C _ _ 把 A 拖到 B 的右边（target=2）→ _ B A C _
    /// </summary>
    private void InsertInGrid(int from, int to)
    {
        if (from < 0 || from >= KeyMap.Size || to < 0 || to >= KeyMap.Size || from == to) return;
        var moving = _grid[from];
        if (moving == null || moving.Locked) return;          // 空位 / 锁定槽位不能被拖动
        if (_grid[to]?.Locked == true)                        // 锁定槽位不能被挤走
        {
            Logger.Info($"目标键位已锁定，不能占用: {_grid[to]!.Name}");
            return;
        }

        _grid[from] = null;                                   // 源位置留空

        // 从 to 起把内容右移一格：锁定格原地不动，被它挡下的内容继续往右找位置
        var carry = moving;
        for (int i = to; i < KeyMap.Size && carry != null; i++)
        {
            if (_grid[i]?.Locked == true) continue;           // 锁定格不动
            var next = _grid[i];
            _grid[i] = carry;
            carry = next;                                     // 原内容继续往右推
        }

        // carry 非空 = 被挤出最右边界 → 放进"未入网格"区（并随布局落盘，记忆化）
        CommitGrid(moving, carry);
    }

    /// <summary>
    /// 把"未入网格"区的槽位拖回网格：插到 to 键位，后面的内容往后挤（锁定格不动）。
    /// 被挤出的内容回到"未入网格"区；被拖进来的槽位从溢出列表移除。
    /// </summary>
    private void InsertOverflowIntoGrid(ResolvedSlot moving, int to)
    {
        if (to < 0 || to >= KeyMap.Size) return;
        if (_grid[to]?.Locked == true) { Logger.Info($"目标键位已锁定，不能占用: {_grid[to]!.Name}"); return; }

        var carry = moving;
        for (int i = to; i < KeyMap.Size && carry != null; i++)
        {
            if (_grid[i]?.Locked == true) continue;      // 锁定格不动
            var next = _grid[i];
            _grid[i] = carry;
            carry = next;
        }

        RemoveFromOverflow(moving);
        CommitGrid(moving, carry);
    }

    /// <summary>
    /// 网格 → 未入网格：把 from 键位的槽位移出网格，放进"未入网格"区（记忆化）。
    /// 源键位留空（其它格不动），锁定槽位不能移出。
    /// </summary>
    private void MoveGridSlotToOverflow(int from)
    {
        if (from < 0 || from >= KeyMap.Size) return;
        var s = _grid[from];
        if (s == null) return;
        if (s.Locked) { Logger.Info($"锁定槽位不能移出网格: {s.Name}"); return; }

        _grid[from] = null;                 // 源键位留空
        Logger.Info($"网格 → 未入网格: {s.Name}");
        CommitGrid(null, s);                // s 作为"被挤出"加入未入网格
    }

    /// <summary>把某个槽位从"未入网格"列表里移除（它已经进网格了）。</summary>
    private void RemoveFromOverflow(ResolvedSlot slot)
    {
        for (int i = _vm.Overflow.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_vm.Overflow[i].Slot, slot))
            {
                _vm.Overflow.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>把 _grid 的键位固化到各槽位，重建 _topSlots / 视图并落盘。</summary>
    /// <param name="select">重建后要选中的槽位。</param>
    /// <param name="pushedOut">被挤出网格的槽位，放进"未入网格"区并落盘（记忆化）。</param>
    private void CommitGrid(ResolvedSlot? select = null, ResolvedSlot? pushedOut = null)
    {
        var slots = new List<ResolvedSlot>();
        for (int i = 0; i < KeyMap.Size; i++)
        {
            var s = _grid[i];
            if (s == null) continue;
            s.Position = i;
            slots.Add(s);
        }

        if (pushedOut != null)
        {
            pushedOut.Position = -1;        // <0 = 强制留在"未入网格"区（记忆化）
            slots.Add(pushedOut);
            Logger.Info($"挤出网格 → 未入网格: {pushedOut.Name}");
        }

        foreach (var r in _vm.Overflow)
        {
            if (r.Slot.Position is null or >= 0) r.Slot.Position = -1;   // 溢出项没有键位
            slots.Add(r.Slot);
        }

        _topSlots = slots;
        BuildRows(_topSlots);
        if (select != null) SelectTopIndex(TopIndexOf(select));
        LayoutChanged?.Invoke(_topSlots);
    }

    private void CommitLayout(IReadOnlyList<ResolvedSlot> slots, int selectIndex = -1, bool compact = true)
    {
        _topSlots = slots;
        // 结构变化（删除/成组/解散）后清掉未锁定槽位的显式位置，让它们自动往前补；
        // 纯重命名不该打乱已经拖出来的空位，所以传 compact:false。
        if (compact)
            foreach (var s in slots)
                if (!s.Locked && s.Position is >= 0) s.Position = null;   // -1（强制未入网格）保留

        _openGroup = null;
        _openGroupIndex = -1;
        _vm.Level = SelectorLevel.Top;
        _vm.Breadcrumb = "";
        BuildRows(_topSlots);

        if (selectIndex >= 0) SelectTopIndex(selectIndex);
        LayoutChanged?.Invoke(_topSlots);
    }

    /// <summary>UI 行在一级列表 _topSlots 里的索引（按"行当前所在位置"反查）；找不到返回 -1。
    /// 走"行在网格 / 溢出列表的位置 → 一级索引"的路径，不依赖 row.Slot 这个可能已过期的引用。</summary>
    private int TopIndexOf(SlotViewModel row)
    {
        int full = CurrentIndexOf(row);
        if (full < 0) return -1;
        return TopIndexOfFull(full);
    }

    /// <summary>槽位在一级列表 _topSlots 里的索引（按引用）；找不到返回 -1。</summary>
    private int TopIndexOf(ResolvedSlot slot)
    {
        for (int i = 0; i < _topSlots.Count; i++)
            if (ReferenceEquals(_topSlots[i], slot)) return i;
        return -1;
    }

    /// <summary>按 _topSlots 列表索引选中对应的行（网格或溢出）。</summary>
    private void SelectTopIndex(int topIndex)
    {
        if (topIndex < 0 || topIndex >= _topSlots.Count) return;
        var slot = _topSlots[topIndex];
        foreach (var r in _vm.Slots) if (!r.IsEmpty && ReferenceEquals(r.Slot, slot)) { Select(r); return; }
        foreach (var r in _vm.Overflow) if (ReferenceEquals(r.Slot, slot)) { Select(r); return; }
    }

    /// <summary>组内操作落盘（排序 / 移出），并**留在二级**。</summary>
    private void CommitGroupReorder(IReadOnlyList<ResolvedSlot> slots, int groupIndex, int selectIndex)
    {
        _topSlots = slots;
        _openGroup = slots[groupIndex];
        _openGroupIndex = groupIndex;
        _vm.Level = SelectorLevel.InGroup;
        _vm.Breadcrumb = $"{KeyMap.LabelOf(groupIndex)} › {_openGroup.Name}";
        BuildGroupRows(_openGroup);

        // 选中项可能被这次操作移走了（移出分组），简单夹到合法范围即可
        int idx = selectIndex;
        if (idx < 0 || idx >= _gridCount)
        {
            idx = Math.Min(Math.Max(0, idx), Math.Max(0, _gridCount - 1));
        }
        if (_gridCount > 0) Select(_vm.Slots[idx]);
        LayoutChanged?.Invoke(_topSlots);
    }

    // ----------------------------------------------------------
    //  右键菜单：重命名 / 关闭程序 / 解散分组 / 移出分组
    //  画在自己窗口的可视树里（PART_Menu），不用 ContextMenu/Popup，
    //  避免 Topmost 窗口下弹窗被盖住 / 激活权切换等窗口层问题。
    //  一级和二级都支持。
    // ----------------------------------------------------------

    private void OnListPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 一级 / 二级都支持右键菜单
        if (_vm.Level == SelectorLevel.Search) return;

        var row = HitRow(PART_List, e)
            ?? (PART_Overflow.Visibility == Visibility.Visible ? HitRow(PART_Overflow, e) : null);
        if (row == null) return;

        Select(row);
        e.Handled = true;
        ShowRowMenu(row);
    }

    private void ShowRowMenu(SlotViewModel row)
    {
        _menuRow = row;
        // 重命名：程序组 / 程序组合 / 程序各自的名字
        PART_MenuRename.Content = row.Slot.Kind switch
        {
            SlotKind.Group => "重命名程序组",
            SlotKind.Combination => "重命名程序组合",
            _ => "重命名程序",
        };
        // 关闭程序只对单个程序有意义
        PART_MenuClose.Visibility = row.Slot.Kind == SlotKind.Window
            ? Visibility.Visible : Visibility.Collapsed;
        // 删除程序组：只一级 + 必须是程序组
        PART_MenuDissolve.Visibility = (_vm.Level == SelectorLevel.Top && row.Slot.Kind == SlotKind.Group)
            ? Visibility.Visible : Visibility.Collapsed;
        // 解散程序组合：一级 / 二级都支持
        PART_MenuDissolveCombo.Visibility = row.Slot.Kind == SlotKind.Combination
            ? Visibility.Visible : Visibility.Collapsed;
        // 移出只二级；只要组里不止 1 个成员就能拆（按窗口/子项拆）
        bool canRemove = _openGroup != null && _vm.Level == SelectorLevel.InGroup
            && (_openGroup.Children?.Count > 1 || (_openGroup.Children == null && _openGroup.Windows.Count > 1));
        PART_MenuRemove.Visibility = canRemove ? Visibility.Visible : Visibility.Collapsed;

        var p = Mouse.GetPosition(PART_Grid);
        PART_Menu.Visibility = Visibility.Visible;
        PART_Menu.UpdateLayout();

        // 贴边向内收
        double x = Math.Min(p.X, Math.Max(0, PART_Grid.ActualWidth - PART_Menu.ActualWidth - 4));
        double y = Math.Min(p.Y, Math.Max(0, PART_Grid.ActualHeight - PART_Menu.ActualHeight - 4));
        PART_Menu.Margin = new Thickness(x, y, 0, 0);

        Logger.Info($"右键菜单[{_vm.Level}]: {row.Name}");
    }

    private void HideRowMenu()
    {
        _menuRow = null;
        PART_Menu.Visibility = Visibility.Collapsed;
    }

    private void OnMenuRenameClick(object sender, RoutedEventArgs e)
    {
        var row = _menuRow;
        HideRowMenu();
        if (row == null) return;
        if (_vm.Level == SelectorLevel.InGroup) RenameMember(row);
        else RenameSlot(row);
    }

    private void OnMenuCloseClick(object sender, RoutedEventArgs e)
    {
        var row = _menuRow;
        HideRowMenu();
        if (row != null) CloseWindowFromSlot(row);
    }

    private void OnMenuDissolveClick(object sender, RoutedEventArgs e)
    {
        var row = _menuRow;
        HideRowMenu();
        if (row != null) DissolveGroup(row);
    }

    private void OnMenuDissolveComboClick(object sender, RoutedEventArgs e)
    {
        var row = _menuRow;
        HideRowMenu();
        if (row != null) DissolveCombination(row);
    }

    private void OnMenuRemoveClick(object sender, RoutedEventArgs e)
    {
        var row = _menuRow;
        HideRowMenu();
        if (row != null) RemoveFromGroup(row);
    }

    /// <summary>点击格子右上角的锁：锁定 / 解锁。锁定时记住当前键位，排序 / 删除都不会移动它。</summary>
    private void OnLockToggleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;      // 别让这次点击冒泡到 ListBox 触发"确认切换"
        if (sender is not FrameworkElement fe || fe.DataContext is not SlotViewModel row) return;
        if (row.IsEmpty) return;

        int pos = CurrentIndexOf(row);
        if (pos < 0 || pos >= KeyMap.Size) return;      // 只有网格里的槽位能锁定

        // 二级：组内成员走自己的锁定通道（手工组 → Children[i]；自动折叠组 → Members[i]）
        if (_vm.Level == SelectorLevel.InGroup && _openGroup != null && _openGroupIndex >= 0)
        {
            ToggleChildLock(row, pos);
            return;
        }

        var slots = _topSlots.ToList();
        int idx = TopIndexOf(row);
        if (idx < 0) return;

        bool locked = !slots[idx].Locked;

        // 已关闭的占位槽位解锁 = 它的历史使命结束，立即从布局里移除（要落盘，否则下次唤起又冒出来）
        if (!locked && slots[idx].IsClosed)
        {
            RemoveGhostRow(row);
            Activate();
            Focus();
            return;
        }

        // 锁定 / 解锁都保留当前位置；且不让其它未锁定槽位被压缩（compact:false）
        var updated = slots[idx].WithLock(locked, pos);
        if (locked)
        {
            // 记录"未开则启动"的可执行路径：窗口以后关掉，按这个键位还能拉起来。
            // explorer 窗口另走 Shell COM 拿真实浏览路径（标题会被 Windows 截断，不可靠）。
            updated = SlotEditor.WithLaunchInfo(updated, ResolveExePath,
                w => ExplorerPathResolver.GetPath(w.Hwnd));
        }
        slots[idx] = updated;
        Logger.Info($"{(locked ? "锁定" : "解锁")}: {row.Name} @ {KeyMap.LabelOf(pos)}");
        CommitLayout(slots, idx, compact: false);
        Activate();
        Focus();
    }

    /// <summary>二级锁定切换：调 <see cref="SlotEditor.SetChildLock"/>，并留在二级。</summary>
    private void ToggleChildLock(SlotViewModel row, int pos)
    {
        if (_openGroup == null || _openGroupIndex < 0) return;

        int childIdx = _vm.Slots.IndexOf(row);
        if (childIdx < 0) return;

        bool locked = !row.Slot.Locked;

        // 已关闭的占位成员解锁 = 直接从组里移除
        if (!locked && row.Slot.IsClosed)
        {
            RemoveGhostRow(row);
            Activate();
            Focus();
            return;
        }

        // 锁定时顺带记录可执行路径：成员窗口以后关掉，按键还能重新启动
        string? exePath = locked && row.Slot.Windows is { Count: > 0 }
            ? ResolveExePath(row.Slot.Windows[0])
            : null;
        var result = SlotEditor.SetChildLock(_topSlots, _openGroupIndex, childIdx, locked, pos,
            exePath, w => ExplorerPathResolver.GetPath(w.Hwnd));
        if (result == null) return;

        Logger.Info($"二级{(locked ? "锁定" : "解锁")}: {row.Name} @ {KeyMap.LabelOf(pos)}");
        CommitGroupReorder(result, _openGroupIndex, childIdx);
        Activate();
        Focus();
    }

    /// <summary>一级重命名：改槽位本身（窗口或组）。</summary>
    private void RenameSlot(SlotViewModel row)
    {
        int idx = TopIndexOf(row.Slot);
        if (idx < 0) return;

        var slot = _topSlots[idx];
        string title = slot.Kind switch
        {
            SlotKind.Group => "重命名程序组",
            SlotKind.Combination => "重命名程序组合",
            _ => "重命名程序",
        };

        string? result;
        _suppressAutoClose = true;
        SuspendIndexCaptureChanged?.Invoke(true);
        try { result = RenameDialog.Show(this, title, slot.Name); }
        finally
        {
            SuspendIndexCaptureChanged?.Invoke(false);
            _suppressAutoClose = false;
        }
        if (result == null) return;

        var slots = _topSlots.ToList();
        slots[idx] = slot.WithName(result);
        Logger.Info($"重命名: {slot.Name} → {(string.IsNullOrWhiteSpace(result) ? "(默认名)" : result)}");
        CommitLayout(slots, idx, compact: false);
        Activate();
        Focus();
    }

    /// <summary>
    /// 二级重命名：改的是**这一行的程序**（窗口）的显示名，不是整个组。
    ///
    /// 组本身的名字在一级右键组行改。二级里每行都是组内一个窗口，
    /// 用户右键的就是它，改的也应该是它。名字存在成员描述 <see cref="MemberSpec.DisplayName"/> 上，
    /// 跟着 Members 一起落盘，下次解析时恢复。
    /// </summary>
    private void RenameMember(SlotViewModel memberRow)
    {
        if (_vm.Level != SelectorLevel.InGroup || _openGroup == null || _openGroupIndex < 0) return;

        int memberIdx = _vm.Slots.IndexOf(memberRow);
        if (memberIdx < 0) return;

        string title = memberRow.Slot.Kind == SlotKind.Combination ? "重命名程序组合" : "重命名程序";

        string? result;
        _suppressAutoClose = true;
        SuspendIndexCaptureChanged?.Invoke(true);
        try { result = RenameDialog.Show(this, title, memberRow.Slot.Name); }
        finally
        {
            SuspendIndexCaptureChanged?.Invoke(false);
            _suppressAutoClose = false;
        }
        if (result == null) return;

        // 手工程序组：改子项本身
        if (_openGroup.Children != null)
        {
            var updated = SlotEditor.RenameChild(_topSlots, _openGroupIndex, memberIdx, result);
            if (updated == null) return;
            _topSlots = updated;
            _openGroup = updated[_openGroupIndex];
            BuildGroupRows(_openGroup);
            if (memberIdx < _vm.Slots.Count) Select(_vm.Slots[memberIdx]);
            LayoutChanged?.Invoke(_topSlots);
            Activate();
            Focus();
            return;
        }

        // 自动折叠组：物化 Members，把显示名落到被点的那一行
        if (memberIdx >= _openGroup.Windows.Count) return;

        var newMembers = new List<MemberSpec>(_openGroup.Windows.Count);
        for (int i = 0; i < _openGroup.Windows.Count; i++)
        {
            var wi = _openGroup.Windows[i];
            string? display = _openGroup.Members is { Count: > 0 } && i < _openGroup.Members.Count
                ? _openGroup.Members[i].DisplayName
                : null;
            if (i == memberIdx)
                display = string.IsNullOrWhiteSpace(result) ? null : result;
            newMembers.Add(new MemberSpec(wi.ProcessName, wi.Title) { DisplayName = display, Hwnd = (long)wi.Hwnd });
        }

        var renamed = new ResolvedSlot
        {
            Kind = _openGroup.Kind,
            Name = _openGroup.Name,
            CustomName = _openGroup.CustomName,
            Windows = _openGroup.Windows,
            Processes = _openGroup.Processes,
            IsOverflow = _openGroup.IsOverflow,
            MemberOrder = _openGroup.MemberOrder,
            Members = newMembers,
        };

        var slots = _topSlots.ToList();
        slots[_openGroupIndex] = renamed;
        _topSlots = slots;                                       // 关键：回写到字段，否则下次唤起还是旧名
        _openGroup = renamed;
        BuildGroupRows(renamed);
        if (memberIdx < _vm.Slots.Count) Select(_vm.Slots[memberIdx]);
        LayoutChanged?.Invoke(_topSlots);
        Activate();
        Focus();
    }

    /// <summary>删除程序组：只删容器，成员摊回一级。</summary>
    private void DissolveGroup(SlotViewModel row)
    {
        if (row.Slot.Kind != SlotKind.Group) return;
        int idx = TopIndexOf(row.Slot);
        if (idx < 0) return;
        if (row.Slot.Locked) { Logger.Info($"已锁定的程序组不能解散: {row.Name}"); return; }

        bool overflow = GridPinOf(row.Slot) < 0;    // 在未入网格区：拆出的成员也要留在那里
        var before = _topSlots.Count;
        var slots = SlotEditor.DeleteGroup(_topSlots, idx);
        int pieceCount = slots.Count - (before - 1);
        if (overflow)
            for (int i = idx; i < idx + pieceCount && i < slots.Count; i++) slots[i].Position = -1;
        Logger.Info($"删除程序组: {row.Name} → {before} 个槽位变 {slots.Count} 个");
        CommitLayout(slots, idx, compact: !overflow);
    }

    /// <summary>解散程序组合：一级摊成顶级程序槽位；二级摊成组内程序子项。</summary>
    private void DissolveCombination(SlotViewModel row)
    {
        if (row.Slot.Kind != SlotKind.Combination) return;
        if (row.Slot.Locked) { Logger.Info($"已锁定的程序组合不能解散: {row.Name}"); return; }

        if (_vm.Level == SelectorLevel.InGroup)
        {
            if (_openGroup == null || _openGroupIndex < 0) return;
            int cidx = _vm.Slots.IndexOf(row);       // 组内子项索引
            if (cidx < 0) return;
            var result = SlotEditor.DissolveCombinationInGroup(_topSlots, _openGroupIndex, cidx);
            if (result == null) return;
            Logger.Info($"解散程序组合(组内): {row.Name}");
            CommitGroupReorder(result, _openGroupIndex, cidx);
            return;
        }

        int idx = TopIndexOf(row.Slot);
        if (idx < 0) return;
        bool overflow = GridPinOf(row.Slot) < 0;    // 在未入网格区：拆出的成员也要留在那里
        var before = _topSlots.Count;
        var slots = SlotEditor.DissolveCombination(_topSlots, idx);
        int pieceCount = slots.Count - (before - 1);
        if (overflow)
            for (int i = idx; i < idx + pieceCount && i < slots.Count; i++) slots[i].Position = -1;
        Logger.Info($"解散程序组合: {row.Name}");
        CommitLayout(slots, idx, compact: !overflow);
    }

    /// <summary>左上角"+"：新建一个空程序组，之后把程序/程序组合拖到它上面即可加入。</summary>
    private void OnAddGroupClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Level != SelectorLevel.Top) return;

        string? name;
        _suppressAutoClose = true;
        SuspendIndexCaptureChanged?.Invoke(true);
        try { name = RenameDialog.Show(this, "新建程序组", "新程序组"); }
        finally
        {
            SuspendIndexCaptureChanged?.Invoke(false);
            _suppressAutoClose = false;
        }
        if (name == null) return;   // 取消

        var slots = SlotEditor.CreateEmptyGroup(
            _topSlots, string.IsNullOrWhiteSpace(name) ? "新程序组" : name);
        Logger.Info($"新建程序组: {name}");
        CommitLayout(slots, slots.Count - 1);
        Activate();
        Focus();
    }

    /// <summary>"未入网格"区的"+"：新建一个空程序组，直接放进未入网格区。</summary>
    private void OnAddOverflowGroupClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Level != SelectorLevel.Top) return;

        string? name;
        _suppressAutoClose = true;
        SuspendIndexCaptureChanged?.Invoke(true);
        try { name = RenameDialog.Show(this, "新建程序组", "新程序组"); }
        finally
        {
            SuspendIndexCaptureChanged?.Invoke(false);
            _suppressAutoClose = false;
        }
        if (name == null) return;   // 取消

        var slots = SlotEditor.CreateEmptyGroup(
            _topSlots, string.IsNullOrWhiteSpace(name) ? "新程序组" : name);
        slots[^1].Position = -1;    // 强制留在未入网格区
        Logger.Info($"在未入网格新建程序组: {name}");
        CommitLayout(slots, slots.Count - 1, compact: false);
        Activate();
        Focus();
    }

    /// <summary>
    /// 关闭这个槽位代表的那个窗口（WM_CLOSE，目标程序自己处理）。
    /// **不关闭选择器**——用户可以继续在界面里操作、连续关掉多个程序。
    ///   未锁定槽位：立即从视图消失（下次唤起时按实际窗口重新解析，同样不会再出现）；
    ///   锁定槽位：**不消失**，立即就地转成"未运行"占位，之后按这个键位还能重新启动。
    /// "关闭程序"点在已是"未运行"的占位上 = 移除占位。
    /// </summary>
    private void CloseWindowFromSlot(SlotViewModel row)
    {
        var w = row.Slot.SingleWindow;
        if (w == null || w.Hwnd == IntPtr.Zero)
        {
            // 占位槽位没有可关闭的窗口：右键"关闭程序"= 移除占位（解锁也能达到同样效果）
            Logger.Info($"移除占位槽位: {row.Name}");
            RemoveGhostRow(row);
            Activate();
            Focus();
            return;
        }
        Logger.Info($"关闭程序: {row.Name} (hwnd=0x{w.Hwnd:X})");
        PostMessage(w.Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        if (row.Slot.Locked) MarkRowClosed(row, w);
        else RemoveRowFromView(row);
        Activate();
        Focus();
    }

    /// <summary>
    /// 锁定槽位的窗口被关闭后**立即**就地转成"未运行"占位（不落盘——布局定义没变，
    /// 下次唤起时 LayoutResolver 会从锁定定义里解析出同样的占位）。
    /// 一级和二级都支持；全部按行索引 / 实时引用定位，不依赖可能过期的 row.Slot。
    /// </summary>
    private void MarkRowClosed(SlotViewModel row, WindowInfo closed)
    {
        // 二级：替换组内这个成员
        if (_vm.Level == SelectorLevel.InGroup && _openGroup != null && _openGroupIndex >= 0)
        {
            int memberIdx = _vm.Slots.IndexOf(row);
            if (memberIdx < 0) return;
            var group = _openGroupIndex < _topSlots.Count ? _topSlots[_openGroupIndex] : null;
            if (group == null) return;

            ResolvedSlot rebuilt;
            if (group.Children != null)
            {
                var children = group.Children.ToList();
                if (memberIdx >= children.Count) return;
                children[memberIdx] = children[memberIdx].WithWindows(new[] { GhostOf(children[memberIdx], closed) });
                rebuilt = group.WithChildren(children);
            }
            else
            {
                if (memberIdx >= group.Windows.Count) return;
                var windows = group.Windows.ToList();
                windows[memberIdx] = GhostOfWindow(closed);
                rebuilt = group.WithWindows(windows);
            }

            int gi = _openGroupIndex;
            var slots = _topSlots.ToList();
            slots[gi] = rebuilt;
            _topSlots = slots;
            _openGroup = rebuilt;
            BuildGroupRows(rebuilt);
            SelectClamped(memberIdx);
            return;
        }

        // 一级：替换槽位（单窗口槽位整格转占位；组/多窗口槽位只把被关的那个成员转占位）
        int topIdx = TopIndexOf(row);
        if (topIdx < 0) return;
        var live = _topSlots[topIdx];
        ResolvedSlot updated;
        if (live.Windows.Count > 1)
        {
            var windows = live.Windows.ToList();
            int wi = windows.FindIndex(x => x.Hwnd == closed.Hwnd);
            if (wi < 0) return;
            windows[wi] = GhostOfWindow(closed);
            updated = live.WithWindows(windows);
        }
        else
        {
            updated = live.WithWindows(new[] { GhostOf(live, closed) });
        }
        var slotList = _topSlots.ToList();
        slotList[topIdx] = updated;
        _topSlots = slotList;
        BuildRows(_topSlots);
        SelectTopIndex(topIdx);
    }

    /// <summary>
    /// 移除"未运行"占位行。占位是锁定定义的产物，**必须落盘**才不会下次唤起又冒出来。
    /// 解锁占位、右键"关闭程序"点在占位上都走这里。
    /// </summary>
    private void RemoveGhostRow(SlotViewModel row)
    {
        // 二级：从组里移除这个占位成员
        if (_vm.Level == SelectorLevel.InGroup && _openGroup != null && _openGroupIndex >= 0)
        {
            int memberIdx = _vm.Slots.IndexOf(row);
            if (memberIdx < 0) return;
            var result = SlotEditor.RemoveMemberFromGroup(_topSlots, _openGroupIndex, memberIdx);
            if (result == null) return;
            Logger.Info($"移除组内占位: {row.Name}");
            CommitGroupReorder(result, _openGroupIndex, Math.Max(0, memberIdx - 1));
            return;
        }

        int idx = TopIndexOf(row);
        if (idx < 0) return;
        Logger.Info($"移除占位槽位: {row.Name}");
        CommitLayout(SlotEditor.RemoveAt(_topSlots, idx), Math.Max(0, idx - 1), compact: false);
    }

    /// <summary>整槽位转占位：Hwnd=0，标题沿用用户改过的名字，没有就用被关窗口的标题。</summary>
    private static WindowInfo GhostOf(ResolvedSlot slot, WindowInfo closed)
    {
        string proc = closed.ProcessName;
        string title = !string.IsNullOrWhiteSpace(slot.CustomName) ? slot.CustomName
            : !string.IsNullOrWhiteSpace(slot.Name) ? slot.Name
            : closed.Title;
        return new WindowInfo(IntPtr.Zero, title, proc, 0, false);
    }

    /// <summary>组内单个成员转占位：标题沿用被关窗口的实际标题。</summary>
    private static WindowInfo GhostOfWindow(WindowInfo closed) =>
        new(IntPtr.Zero, closed.Title, closed.ProcessName, 0, false);

    /// <summary>把一个槽位从当前视图里去掉（不改持久化布局）。</summary>
    private void RemoveRowFromView(SlotViewModel row)
    {
        // 二级：从组里移除这个成员
        if (_vm.Level == SelectorLevel.InGroup && _openGroup != null && _openGroupIndex >= 0)
        {
            int memberIdx = _vm.Slots.IndexOf(row);
            if (memberIdx < 0) return;
            var result = SlotEditor.RemoveMemberFromGroup(_topSlots, _openGroupIndex, memberIdx);
            if (result == null) return;
            _topSlots = result;

            if (_openGroupIndex >= _topSlots.Count) { GoBack(); return; }
            var g = _topSlots[_openGroupIndex];
            // 自动组降到单个窗口 → 回一级；组被清空也回一级，别停在空列表上
            bool empty = g.Children != null ? g.Children.Count == 0 : g.Windows.Count == 0;
            if (g.Kind != SlotKind.Group || empty) { GoBack(); return; }

            _openGroup = g;
            BuildGroupRows(g);
            SelectClamped(memberIdx);
            return;
        }

        // 一级：直接移除；未锁定槽位清掉显式位置，自动往前补
        int idx = TopIndexOf(row.Slot);
        if (idx < 0) return;
        _topSlots = SlotEditor.RemoveAt(_topSlots, idx);
        foreach (var s in _topSlots)
            if (!s.Locked) s.Position = null;
        BuildRows(_topSlots);
        SelectTopIndex(Math.Min(idx, _topSlots.Count - 1));
    }

    private void SelectClamped(int fullIdx)
    {
        if (fullIdx < 0) fullIdx = 0;
        // 从期望位置往后找第一个非空；没有就回退
        for (int i = fullIdx; i < _vm.Slots.Count; i++)
            if (!_vm.Slots[i].IsEmpty) { Select(_vm.Slots[i]); return; }
        if (_vm.Overflow.Count > 0) { Select(_vm.Overflow[0]); return; }
        for (int i = _vm.Slots.Count - 1; i >= 0; i--)
            if (!_vm.Slots[i].IsEmpty) { Select(_vm.Slots[i]); return; }
        UpdatePreview();
    }

    /// <summary>
    /// 二级"移出分组"：把这个成员单独拆出来成为一个新槽位，紧跟在该组之后。
    /// 手工组移出子项；自动折叠组按窗口拆。
    /// </summary>
    private void RemoveFromGroup(SlotViewModel memberRow)
    {
        if (_vm.Level != SelectorLevel.InGroup || _openGroup == null || _openGroupIndex < 0) return;
        int idx = _vm.Slots.IndexOf(memberRow);
        if (idx < 0) return;
        // 锁定的成员不能被移出（一级"锁定槽位不能拖动"的对齐）
        if (memberRow.Slot.Locked)
        {
            Logger.Info($"已锁定的成员不能移出: {memberRow.Name}");
            return;
        }

        List<ResolvedSlot>? result;
        if (_openGroup.Children != null)
        {
            result = SlotEditor.MoveChildOutOfGroup(_topSlots, _openGroupIndex, idx);
            if (result == null) { Logger.Info("移出失败：只剩一个成员"); return; }
            Logger.Info($"移出子项: {memberRow.Name} ← {_openGroup.Name}");
        }
        else
        {
            var w = memberRow.Slot.SingleWindow;
            if (w == null) return;
            result = SlotEditor.MoveWindowOutOfGroup(_topSlots, _openGroupIndex, w);
            if (result == null) { Logger.Info($"移出分组失败: {w.Title} 是该组唯一成员"); return; }
            Logger.Info($"移出分组: {w.Title} ({w.ProcessName}) ← {_openGroup.Name}");
        }
        CommitGroupReorder(result, _openGroupIndex, idx);
    }

    // ----------------------------------------------------------
    //  Ghost 拖动特效
    // ----------------------------------------------------------

    private void ShowDragGhost()
    {
        var cell = CurrentRowAt(_dragSourceIndex);
        if (cell == null) return;
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
            Width = 96, Height = 96,
            Content = BuildGhostContent(cell),
        };
        var sp = System.Windows.Forms.Cursor.Position;
        var (dipX, dipY) = ScreenPxToDip(sp);
        _ghostWindow.Left = dipX - 48;
        _ghostWindow.Top = dipY - 48;
        _ghostWindow.Show();

        // 关键：Topmost 窗口正好压在光标底下时，光标消息会被它截走，
        // SelectorWindow 的 MouseMove 就停在那，drop feedback 卡死在源行上。
        // WS_EX_TRANSPARENT 让 Win32 直接把鼠标事件穿透到下层窗口。
        // WPF 的 IsHitTestVisible 只管窗口**内部**命中测试，管不了 Win32 谁收消息。
        var ghostHwnd = new System.Windows.Interop.WindowInteropHelper(_ghostWindow).Handle;
        WindowActivator.MakeClickThrough(ghostHwnd);

        StartDragFollowTimer();
    }

    private void UpdateGhostFromOs()
    {
        if (_ghostWindow == null) return;
        var (dipX, dipY) = ScreenPxToDip(System.Windows.Forms.Cursor.Position);
        _ghostWindow.Left = dipX - 48;
        _ghostWindow.Top = dipY - 48;
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
            // 用 OS 鼠标位置直接驱动 ghost 与 drop feedback，不依赖 MouseMove 事件是否送达本窗口。
            // ghost 是 Topmost 窗口，鼠标压在它底下时 Win32 会把消息截给它，
            // SelectorWindow 的 MouseMove 因此收不到——只有定时器轮询能保证实时。
            UpdateGhostFromOs();
            if (_dragSourceIndex >= 0) UpdateDropFeedback();
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
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (cell.Icon != null)
        {
            var icon = new System.Windows.Controls.Image
            {
                Source = cell.Icon,
                Stretch = Stretch.Uniform,
                Width = 40, Height = 40,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            Grid.SetRow(icon, 0);
            grid.Children.Add(icon);
        }

        var title = new TextBlock
        {
            Text = cell.IsContainer ? $"{cell.Name}  {cell.CountBadge}" : cell.Name,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 221, 221)),
            FontSize = 11,
            TextAlignment = System.Windows.TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 0, 4, 4),
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

    // 关窗口（WM_CLOSE）——让目标程序自己处理，graceful 退出
    private const uint WM_CLOSE = 0x0010;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
