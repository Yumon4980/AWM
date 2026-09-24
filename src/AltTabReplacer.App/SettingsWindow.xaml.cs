using System;
using System.Windows;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;
using AltTabReplacer.ViewModels;

namespace AltTabReplacer;

/// <summary>
/// 设置窗口。左列分类导航 + 右列详情面板；已实现「主题」「输入框聚焦」分类。
/// 由 App 创建（单例），通过托盘菜单唤起。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly FocusTargetService _focusTargets;
    private FocusCaptureSession? _captureSession;

    public SettingsWindow(Settings settings, FocusTargetService focusTargets)
    {
        InitializeComponent();

        _focusTargets = focusTargets;
        _vm = new SettingsViewModel(settings, focusTargets);
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

    /// <summary>
    /// 进入"捕获输入框"模式：设置窗口先藏起来（别挡住目标程序），
    /// 每个显示器一个遮罩接管该屏的下一次左键点击；结束（成功或取消）后回来刷新列表。
    /// </summary>
    private void OnCaptureFocusTarget(object sender, RoutedEventArgs e)
    {
        if (_captureSession != null) return;   // 已在捕获中

        Hide();
        _captureSession = FocusCaptureSession.Start(_focusTargets);
        _captureSession.Finished += OnCaptureFinished;
    }

    private void OnCaptureFinished(FocusTargetEntry? entry)
    {
        // 会话触发 Finished 时已关闭全部遮罩；事件在 UI 线程上
        _captureSession = null;
        Show();
        Activate();
        _vm.LoadFocusTargets();
        Core.Infrastructure.Logger.Info(entry != null
            ? $"已录制输入框: {entry.Process}"
            : "取消捕获输入框");
    }
}
