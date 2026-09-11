using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;

namespace AltTabReplacer.ViewModels;

/// <summary>
/// 选择器列表里一行的视图模型。可能是**程序**、**程序组**或**程序组合**。
/// </summary>
public sealed class SlotViewModel : INotifyPropertyChanged
{
    public ResolvedSlot Slot { get; }

    public bool IsGroup => Slot.Kind == SlotKind.Group;
    public bool IsCombination => Slot.Kind == SlotKind.Combination;
    public bool IsWindow => Slot.Kind == SlotKind.Window;

    /// <summary>程序组 / 程序组合都画"容器"外观（色带 + 图标网格 + 计数徽标）。</summary>
    public bool IsContainer => IsGroup || IsCombination;

    public string Name { get; }

    /// <summary>
    /// 计数徽标：程序组 ⊞ N（成员窗口数），程序组合 ▶ N（程序数，含未开的），程序空。
    /// </summary>
    public string CountBadge => IsGroup ? $"⊞ {Slot.Count}"
        : IsCombination ? $"▶ {Slot.Members?.Count ?? Slot.Count}" : "";

    public Visibility GroupVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CombinationVisibility => IsCombination ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WindowVisibility => IsWindow ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ContainerVisibility => IsContainer ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>只有程序组才显示"下一级箭头"。</summary>
    public Visibility ArrowVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>窗口行的单个图标。</summary>
    public BitmapSource? Icon { get; }

    /// <summary>容器行的 2×2 图标网格，最多 4 个。</summary>
    public IReadOnlyList<BitmapSource?> Icons { get; }

    /// <summary>按下这个键即可选中本行。</summary>
    private string _keyLabel = "";
    public string KeyLabel
    {
        get => _keyLabel;
        set { if (_keyLabel == value) return; _keyLabel = value; Notify(); }
    }

    /// <summary>拖动中：本行是"并入"的落点，模板据此画描边。</summary>
    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget == value) return; _isDropTarget = value; Notify(); }
    }

    /// <summary>预览用的代表窗口：程序是它自己，容器是成员里最近激活的那个。</summary>
    public WindowInfo? Representative => Slot.Count > 0 ? Slot.Windows[0] : null;

    public SlotViewModel(ResolvedSlot slot, string keyLabel, BitmapSource? icon, IReadOnlyList<BitmapSource?> icons)
    {
        Slot = slot;
        Name = Truncate(slot.Name, 34);
        _keyLabel = keyLabel;
        Icon = icon;
        Icons = icons;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
