using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;

namespace AltTabReplacer.ViewModels;

/// <summary>
/// 规则编辑器视图模型。
/// 维护 <see cref="Rules"/> 集合 + Add/Remove 命令。
/// 任何单元格修改 → 立刻写回 <see cref="RuleStore"/>，触发热重载。
/// </summary>
public sealed class ConfigViewModel : INotifyPropertyChanged
{
    private readonly RuleStore _store;

    public ObservableCollection<ConfigRuleViewModel> Rules { get; } = new();

    public ConfigViewModel(RuleStore store)
    {
        _store = store;
        Reload();
    }

    public void Reload()
    {
        Rules.Clear();
        foreach (var r in _store.Current)
        {
            var vm = new ConfigRuleViewModel(r);
            vm.OnChanged += OnRuleChanged;
            Rules.Add(vm);
        }
    }

    public ConfigRuleViewModel AddNew()
    {
        var rule = new SortRule
        {
            MatchType = MatchType.ProcessName,
            Pattern = "newapp.exe",
            Priority = 50,
        };
        _store.Add(rule);
        var vm = new ConfigRuleViewModel(rule);
        vm.OnChanged += OnRuleChanged;
        Rules.Add(vm);
        return vm;
    }

    public void Remove(ConfigRuleViewModel vm)
    {
        _store.Remove(vm.Id);
        vm.OnChanged -= OnRuleChanged;
        Rules.Remove(vm);
    }

    private void OnRuleChanged(ConfigRuleViewModel vm)
    {
        _store.Update(vm.ToModel());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
