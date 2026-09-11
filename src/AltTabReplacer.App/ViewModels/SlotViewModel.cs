using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;

namespace AltTabReplacer.ViewModels;

/// <summary>
/// 选择器列表里一行的视图模型。可能是**一个窗口**，也可能是**一个组**。
/// 取代了旧的 WindowCellViewModel（那时只有窗口一种形态）。
/// </summary>
public sealed class SlotViewModel : INotifyPropertyChanged
{
    public ResolvedSlot Slot { get; }

    public bool IsGroup => Slot.Kind == SlotKind.Group;
    public string Name { get; }

    /// <summary>组成员数徽标，窗口行为空。</summary>
    public string CountBadge => IsGroup ? $"⊞ {Slot.Count}" : "";

    public Visibility GroupVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WindowVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>窗口行的单个图标。</summary>
    public BitmapSource? Icon { get; }

    /// <summary>组行的 2×2 图标网格，最多 4 个。</summary>
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

    /// <summary>预览用的代表窗口：窗口行是它自己，组行是组内最近激活的那个。</summary>
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
