using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

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
    /// <summary>当前层级放进 4×4 网格的行（一级：前 16 个槽位；二级：组内成员）。</summary>
    public ObservableCollection<SlotViewModel> Slots { get; } = new();

    /// <summary>放不进网格的槽位（一级超出 16 的部分），显示在网格左侧的纵向列表里。</summary>
    public ObservableCollection<SlotViewModel> Overflow { get; } = new();

    /// <summary>有溢出项时才显示左侧列表列。</summary>
    public Visibility OverflowVisibility =>
        Overflow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
            Notify(nameof(AddGroupVisibility));
        }
    }

    public bool IsSearching => _level == SelectorLevel.Search;

    /// <summary>只有一级才显示"新建程序组"按钮（二级里建的是顶级组，容易迷惑）。</summary>
    public Visibility AddGroupVisibility =>
        _level == SelectorLevel.Top ? Visibility.Visible : Visibility.Collapsed;

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
        set { if (_searchText == value) return; _searchText = value ?? ""; Notify(); }
    }

    public SelectorViewModel()
    {
        Overflow.CollectionChanged += (_, __) => Notify(nameof(OverflowVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
