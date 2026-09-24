using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;

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
    private readonly FocusTargetService _focusTargets;
    private SettingsCategory _selected = null!;

    public SettingsViewModel(Settings settings, FocusTargetService focusTargets)
    {
        _settings = settings;
        _focusTargets = focusTargets;

        // 顺序就是左列从上到下的顺序，第一项 = 主题。
        Categories = new ObservableCollection<SettingsCategory>
        {
            new("主题",   "\uE790", SettingsCategoryKind.Theme),
            new("快捷键", "\uE765", SettingsCategoryKind.KeyShortcut),
            new("布局",   "\uE8A9", SettingsCategoryKind.Layout),
            new("行为",   "\uE713", SettingsCategoryKind.Behavior),
            new("规则",   "\uE8FD", SettingsCategoryKind.Rules),
            new("输入框聚焦", "\uE70F", SettingsCategoryKind.FocusTarget),
        };
        Selected = Categories[0];
        LoadFocusTargets();
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
            Notify(nameof(IsFocusSelected));
            Notify(nameof(IsBehaviorSelected));
            Notify(nameof(IsGenericSelected));
        }
    }

    public bool IsThemeSelected => _selected?.Kind == SettingsCategoryKind.Theme;
    public bool IsFocusSelected => _selected?.Kind == SettingsCategoryKind.FocusTarget;
    public bool IsBehaviorSelected => _selected?.Kind == SettingsCategoryKind.Behavior;
    /// <summary>还没做专属面板的分类（右列显示占位文案）。</summary>
    public bool IsGenericSelected => _selected?.Kind
        is not (SettingsCategoryKind.Theme or SettingsCategoryKind.FocusTarget
            or SettingsCategoryKind.Behavior);

    /// <summary>
    /// 选择器失焦时自动关闭。ZCode 这类会抢前台的应用会让选择器刚弹出就被关掉、
    /// 切换无法完成——关掉此开关后选择器常驻，索引键照常可用（走低层钩子，不依赖焦点）。
    /// </summary>
    public bool HideSelectorOnDeactivate
    {
        get => _settings.Behavior.HideOnWindowChange;
        set
        {
            if (_settings.Behavior.HideOnWindowChange == value) return;
            _settings.Behavior.HideOnWindowChange = value;
            Notify();
            SaveRequested?.Invoke();
        }
    }

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

    /// <summary>切换程序后自动聚焦到录制的输入框（全局开关，默认开）。</summary>
    public bool FocusAfterSwitch
    {
        get => _settings.Behavior.FocusInputAfterSwitch;
        set
        {
            if (_settings.Behavior.FocusInputAfterSwitch == value) return;
            _settings.Behavior.FocusInputAfterSwitch = value;
            Notify();
            SaveRequested?.Invoke();
        }
    }

    /// <summary>已录制的"切换后聚焦输入框"列表（按进程一条）。</summary>
    public ObservableCollection<FocusTargetRowViewModel> FocusTargetRows { get; }
        = new();

    public bool HasFocusTargets => FocusTargetRows.Count > 0;

    /// <summary>从服务重读列表（捕获完成后由设置窗口调用）。</summary>
    public void LoadFocusTargets()
    {
        FocusTargetRows.Clear();
        foreach (var e in _focusTargets.Entries
                     .OrderBy(x => x.Process, StringComparer.OrdinalIgnoreCase))
        {
            FocusTargetRows.Add(new FocusTargetRowViewModel(e, RemoveTarget));
        }
        Notify(nameof(HasFocusTargets));
    }

    private void RemoveTarget(FocusTargetRowViewModel row)
    {
        if (_focusTargets.Remove(row.Process))
            Logger.Info($"已删除聚焦配置: {row.Process}");
        LoadFocusTargets();
    }

    /// <summary>VM 改完设置后请求 App 落盘并应用主题。</summary>
    public event Action? SaveRequested;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>「输入框聚焦」列表的一行：进程 + 控件描述 + 删除按钮。</summary>
public sealed class FocusTargetRowViewModel
{
    public FocusTargetRowViewModel(FocusTargetEntry entry, Action<FocusTargetRowViewModel> onDelete)
    {
        Process = entry.Process;
        Description = FocusTargetService.Describe(entry);
        CapturedAt = entry.CapturedAt ?? "";
        Delete = new DelegateCommand(_ => onDelete(this));
    }

    /// <summary>进程名（不带 .exe）。</summary>
    public string Process { get; }

    /// <summary>录入的控件描述，如 "Edit · addressBar"。</summary>
    public string Description { get; }

    public string CapturedAt { get; }

    public ICommand Delete { get; }
}

/// <summary>最小化的命令实现，设置窗口这种简单场景够用。</summary>
public sealed class DelegateCommand : ICommand
{
    private readonly Action<object?> _execute;
    public DelegateCommand(Action<object?> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
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
    FocusTarget,
}
