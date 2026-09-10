using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.ViewModels;

/// <summary>
/// DataGrid 中单行规则的视图模型。
/// 包装 <see cref="SortRule"/>，每个属性 setter 都会触发 <see cref="OnChanged"/>
/// 让宿主（ConfigViewModel）回写到 RuleStore。
/// </summary>
public sealed class ConfigRuleViewModel : INotifyPropertyChanged
{
    public Guid Id { get; }

    private MatchType _matchType;
    public MatchType MatchType
    {
        get => _matchType;
        set { if (_matchType == value) return; _matchType = value; Notify(); OnChanged?.Invoke(this); }
    }

    private string _pattern = "";
    public string Pattern
    {
        get => _pattern;
        set { if (_pattern == value) return; _pattern = value ?? ""; Notify(); OnChanged?.Invoke(this); }
    }

    private int _priority;
    public int Priority
    {
        get => _priority;
        set { if (_priority == value) return; _priority = value; Notify(); OnChanged?.Invoke(this); }
    }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned == value) return; _isPinned = value; Notify(); OnChanged?.Invoke(this); }
    }

    private bool _isExcluded;
    public bool IsExcluded
    {
        get => _isExcluded;
        set { if (_isExcluded == value) return; _isExcluded = value; Notify(); OnChanged?.Invoke(this); }
    }

    /// <summary>宿主（ConfigViewModel）监听此事件以回写 RuleStore。</summary>
    public event Action<ConfigRuleViewModel>? OnChanged;

    public ConfigRuleViewModel(SortRule rule)
    {
        Id = rule.Id;
        _matchType = rule.MatchType;
        _pattern = rule.Pattern;
        _priority = rule.Priority;
        _isPinned = rule.IsPinned;
        _isExcluded = rule.IsExcluded;
    }

    /// <summary>从当前 VM 状态生成不可变 SortRule。</summary>
    public SortRule ToModel() => new SortRule
    {
        Id = Id,
        MatchType = _matchType,
        Pattern = _pattern,
        Priority = _priority,
        IsPinned = _isPinned,
        IsExcluded = _isExcluded,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
