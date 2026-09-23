using System;
using System.Windows;
using AltTabReplacer.Core.Models;
using AltTabReplacer.ViewModels;

namespace AltTabReplacer;

/// <summary>
/// 设置窗口。左列分类导航 + 右列详情面板；目前只实现了「主题」分类。
/// 由 App 创建（单例），通过 <see cref="SelectorWindow.SettingsRequested"/> 事件唤起。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();

        _vm = new SettingsViewModel(settings);
        DataContext = _vm;

        // VM 改完设置 → 让 App 落盘 + 重新应用主题。
        _vm.SaveRequested += () =>
        {
            try
            {
                SettingsLoader.Save(SettingsLoader.DefaultPath, settings);
                ThemeManager.ApplyTheme(settings.Theme.Mode);
            }
            catch (Exception ex)
            {
                Core.Infrastructure.Logger.Error("保存设置失败", ex);
            }
        };
    }
}