using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.ViewModels;

/// <summary>选择器视图模型：维护 cell 列表 + 当前选中索引。</summary>
public sealed class SelectorViewModel : INotifyPropertyChanged
{
    public ObservableCollection<WindowCellViewModel> Cells { get; } = new();
    private int _selectedIndex;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
