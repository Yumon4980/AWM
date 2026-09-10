using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.ViewModels;

namespace AltTabReplacer;

/// <summary>
/// 规则配置窗口。
/// 由 App 启动后监听次级热键 Ctrl+Alt+R 唤起。
/// </summary>
public partial class ConfigWindow : Window
{
    private readonly ConfigViewModel _vm;
    private readonly string _rulesPath;

    public ConfigWindow(Core.Services.RuleStore store, string rulesPath)
    {
        InitializeComponent();
        _vm = new ConfigViewModel(store);
        _rulesPath = rulesPath;
        DataContext = _vm;
        UpdateStatus();
        _vm.Rules.CollectionChanged += (_, __) => UpdateStatus();
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var added = _vm.AddNew();
        Grid.SelectedItem = added;
        Grid.ScrollIntoView(added);
        Grid.BeginEdit();
    }

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is ConfigRuleViewModel vm)
        {
            _vm.Remove(vm);
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is ConfigRuleViewModel vm)
        {
            vm.Priority += 10;
        }
    }

    private void OnDownClick(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is ConfigRuleViewModel vm)
        {
            vm.Priority -= 10;
        }
    }

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // 让 explorer 选中文件并打开默认编辑器
            Process.Start("explorer.exe", $"/select,\"{_rulesPath}\"");
        }
        catch (Exception ex)
        {
            Logger.Error($"打开 rules.json 失败: {ex.Message}");
            System.Windows.MessageBox.Show($"无法打开: {ex.Message}", "AltTabReplacer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Grid.SelectedItem is ConfigRuleViewModel)
        {
            OnRemoveClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private void UpdateStatus()
    {
        StatusText.Text = $"共 {_vm.Rules.Count} 条规则。文件：{_rulesPath}";
    }
}
