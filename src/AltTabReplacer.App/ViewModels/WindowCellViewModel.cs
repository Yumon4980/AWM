using System.ComponentModel;
using System.Runtime.CompilerServices;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.ViewModels;

/// <summary>
/// 选择器网格中单个 cell 的视图模型。
/// KeyLabel 可在重排时重新分配（绑定 INPC 让 XAML 自动更新）。
/// </summary>
public sealed class WindowCellViewModel : INotifyPropertyChanged
{
    public WindowInfo Info { get; }
    public string Title { get; }

    private string _keyLabel = "";
    public string KeyLabel
    {
        get => _keyLabel;
        set { if (_keyLabel == value) return; _keyLabel = value; Notify(); }
    }

    public System.Windows.Media.Imaging.BitmapSource? Thumbnail { get; }

    public WindowCellViewModel(WindowInfo info, int index, string keyLabel, System.Windows.Media.Imaging.BitmapSource? thumbnail)
    {
        Info = info;
        Title = TruncateTitle(info.Title, 32);
        _keyLabel = keyLabel;
        Thumbnail = thumbnail;
    }

    private static string TruncateTitle(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
