using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;

namespace AltTabReplacer.ViewModels;

/// <summary>选择器当前处在哪一层。</summary>
public enum SelectorLevel
{
    /// <summary>一级：16 个槽位，窗口或组。</summary>
    Top = 0,

    /// <summary>二级：某个组的成员。</summary>
    InGroup = 1,

    /// <summary>搜索模式：跨层级扁平列出所有窗口。</summary>
    Search = 2,
}

public sealed class SelectorViewModel : INotifyPropertyChanged
{
    /// <summary>当前层级要显示的行。</summary>
    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    /// <summary>搜索模式下用的过滤视图。</summary>
    public ICollectionView FilteredSlots { get; }

    private SelectorLevel _level = SelectorLevel.Top;
    public SelectorLevel Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            _level = value;
            Notify();
            Notify(nameof(BreadcrumbVisibility));
            Notify(nameof(IsSearching));
            Notify(nameof(SearchHintVisibility));
        }
    }

    public bool IsSearching => _level == SelectorLevel.Search;

    private string _breadcrumb = "";
    /// <summary>形如 "1 › 浏览器"。</summary>
    public string Breadcrumb
    {
        get => _breadcrumb;
        set { if (_breadcrumb == value) return; _breadcrumb = value; Notify(); }
    }

    public Visibility BreadcrumbVisibility =>
        _level == SelectorLevel.Top ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>只有搜索模式才显示搜索框；平时它只会抢走索引键。</summary>
    public Visibility SearchHintVisibility =>
        _level == SelectorLevel.Search ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>拖动到面包屑上 = 移出该组，拖动期间高亮。</summary>
    private bool _breadcrumbIsDropTarget;
    public bool BreadcrumbIsDropTarget
    {
        get => _breadcrumbIsDropTarget;
        set { if (_breadcrumbIsDropTarget == value) return; _breadcrumbIsDropTarget = value; Notify(); }
    }

    private SlotViewModel? _selected;
    public SlotViewModel? SelectedSlot
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Notify(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? "";
            Notify();
            FilteredSlots.Refresh();
        }
    }

    public SelectorViewModel()
    {
        FilteredSlots = CollectionViewSource.GetDefaultView(Slots);
        FilteredSlots.Filter = FilterPredicate;
    }

    private bool FilterPredicate(object obj)
    {
        if (_level != SelectorLevel.Search || string.IsNullOrEmpty(_searchText)) return true;
        if (obj is not SlotViewModel s) return false;
        if (s.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)) return true;
        return s.Slot.Windows.Any(w =>
            w.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            w.ProcessName.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
