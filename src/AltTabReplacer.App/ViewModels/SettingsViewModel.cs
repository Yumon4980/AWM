using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.ViewModels;

/// <summary>
/// 设置窗口视图模型。直接持有 <see cref="Settings"/> 引用——修改即写回对象，
/// 不复制副本：避免拖出「VM 和 Settings 走偏」的状态机。
///
/// 落盘由调用方决定（修改时通过 <see cref="SaveRequested"/> 让 App 负责写文件）。
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly Settings _settings;
    private SettingsCategory _selected = null!;

    public SettingsViewModel(Settings settings)
    {
        _settings = settings;

        // 顺序就是左列从上到下的顺序，第一项 = 主题。
        Categories = new ObservableCollection<SettingsCategory>
        {
            new("主题",   "\uE790", SettingsCategoryKind.Theme),
            new("快捷键", "\uE765", SettingsCategoryKind.KeyShortcut),
            new("布局",   "\uE8A9", SettingsCategoryKind.Layout),
            new("行为",   "\uE713", SettingsCategoryKind.Behavior),
            new("规则",   "\uE8FD", SettingsCategoryKind.Rules),
        };
        Selected = Categories[0];
    }

    public ObservableCollection<SettingsCategory> Categories { get; }

    public SettingsCategory Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Notify();
            Notify(nameof(IsThemeSelected));
            Notify(nameof(IsRulesSelected));
        }
    }

    public bool IsThemeSelected => _selected?.Kind == SettingsCategoryKind.Theme;
    public bool IsRulesSelected => _selected?.Kind == SettingsCategoryKind.Rules;

    /// <summary>当前选中的主题模式（双向绑定到 ComboBox）。</summary>
    public ThemeMode ThemeMode
    {
        get => _settings.Theme.Mode;
        set
        {
            if (_settings.Theme.Mode == value) return;
            _settings.Theme.Mode = value;
            Notify();
            SaveRequested?.Invoke();
        }
    }

    /// <summary>VM 改完设置后请求 App 落盘并应用主题。</summary>
    public event Action? SaveRequested;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>左侧导航项。</summary>
public sealed class SettingsCategory
{
    public SettingsCategory(string title, string icon, SettingsCategoryKind kind)
    {
        Title = title;
        Icon = icon;
        Kind = kind;
    }
    public string Title { get; }
    /// <summary>Segoe MDL2 Assets 字形字符。</summary>
    public string Icon { get; }
    public SettingsCategoryKind Kind { get; }
}

public enum SettingsCategoryKind
{
    Theme,
    KeyShortcut,
    Layout,
    Behavior,
    Rules,
}