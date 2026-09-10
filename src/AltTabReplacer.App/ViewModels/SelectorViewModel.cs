using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;

namespace AltTabReplacer.ViewModels;

public sealed class SelectorViewModel : INotifyPropertyChanged
{
    /// <summary>所有 cell（原始）。</summary>
    public ObservableCollection<WindowCellViewModel> Cells { get; } = new();

    /// <summary>过滤后的 cell（绑定到 ListBox）。</summary>
    public ICollectionView FilteredCells { get; }

    private WindowCellViewModel? _selectedCell;
    /// <summary>当前选中的 cell（用于右侧预览）。</summary>
    public WindowCellViewModel? SelectedCell
    {
        get => _selectedCell;
        set { if (_selectedCell == value) return; _selectedCell = value; OnPropertyChanged(); }
    }

    private string _searchText = "";
    /// <summary>搜索词（空字符串 = 不过滤）。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? "";
            OnPropertyChanged();
            FilteredCells.Refresh();
        }
    }

    public SelectorViewModel()
    {
        FilteredCells = CollectionViewSource.GetDefaultView(Cells);
        FilteredCells.Filter = FilterPredicate;
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrEmpty(_searchText)) return true;
        if (obj is not WindowCellViewModel c) return false;
        var q = _searchText;
        return c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Info.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
