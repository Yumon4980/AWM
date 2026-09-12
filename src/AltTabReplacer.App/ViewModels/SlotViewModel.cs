using System;
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

    /// <summary>
    /// 预览用的代表窗口：
    ///   程序 = 它自己；
    ///   程序组合 = **第一个成员（M[0]）** 的窗口（找不到才退回第一个打开的窗口）；
    ///   程序组 = 第一个子项的代表窗口；若第一个子项是组合，取其第一个成员的窗口。
    /// 这样预览永远是"第一个程序"，不会被 z-order / 打开顺序漂移影响。
    /// </summary>
    public WindowInfo? Representative
    {
        get
        {
            if (Slot.Count == 0) return null;

            if (Slot.Kind == SlotKind.Combination && Slot.Members is { Count: > 0 })
                return ResolveByFirstMember(Slot.Members[0], Slot.Windows) ?? Slot.Windows[0];

            if (Slot.Kind == SlotKind.Group && Slot.Children is { Count: > 0 })
            {
                var first = Slot.Children[0];
                if (first.Members is { Count: > 0 } && first.Windows.Count > 0)
                    return ResolveByFirstMember(first.Members[0], first.Windows) ?? first.Windows[0];
            }

            return Slot.Windows[0];
        }
    }

    /// <summary>按 (Process, [Title]) 在候选窗口里精确认领一个；标题为空时只比进程。</summary>
    private static WindowInfo? ResolveByFirstMember(MemberSpec spec, IReadOnlyList<WindowInfo> windows)
    {
        foreach (var w in windows)
        {
            if (!string.Equals(w.ProcessName, spec.Process, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(spec.Title)
                && !string.Equals(w.Title, spec.Title, StringComparison.OrdinalIgnoreCase))
                continue;
            return w;
        }
        return null;
    }

    /// <summary>空占位格：只为撑满 4×4 网格、保留键位空间关系，不可选中 / 触发。</summary>
    public bool IsEmpty { get; }

    /// <summary>创建一个空占位格（只有键标）。</summary>
    public static SlotViewModel Empty(string keyLabel) => new(keyLabel);

    private SlotViewModel(string keyLabel)
    {
        Slot = new ResolvedSlot { Kind = SlotKind.Window };
        Name = "";
        _keyLabel = keyLabel;
        Icon = null;
        Icons = Array.Empty<BitmapSource?>();
        IsEmpty = true;
    }

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
