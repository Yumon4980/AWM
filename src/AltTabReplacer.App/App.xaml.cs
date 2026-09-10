using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;
using WinForms = System.Windows.Forms;

namespace AltTabReplacer;

/// <summary>
/// 应用入口。
/// 两种运行模式：
///   1) 默认（无参数）：后台常驻 + 监听热键 Alt+Z
///   2) --config：打开规则配置窗口（前台模式，不监听热键）
/// </summary>
public partial class App : System.Windows.Application
{
    private Settings? _settings;
    private RuleStore? _ruleStore;
    private RuleEngine? _ruleEngine;
    private WindowEnumerator? _enumerator;
    private WindowActivator? _activator;
    private WindowCaptureService? _capture;
    private HotkeyService? _hotkey;
    private HostWindow? _hostWindow;
    private SelectorWindow? _selector;
    private LowLevelKeyboardHook? _llHook;
    private WinForms.NotifyIcon? _trayIcon;
    private volatile bool _selectorActive;
    private string? _rulesPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            Logger.Info("=== AltTabReplacer 启动 ===");

            // 1) 解析命令行
            bool configMode = e.Args.Any(a => string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase));

            // 2) 加载配置
            _settings = SettingsLoader.Load(SettingsLoader.DefaultPath);
            Logger.Info($"配置加载完成: {SettingsLoader.DefaultPath}");

            // 3) 加载规则
            _rulesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AltTabReplacer", "rules.json");
            _ruleStore = new RuleStore(_rulesPath);
            _ruleStore.Load();

            if (configMode)
            {
                RunConfigMode();
                return;
            }

            // 4) 后台模式
            RunBackgroundMode();
        }
        catch (Exception ex)
        {
            Logger.Error("启动失败", ex);
            System.Windows.MessageBox.Show($"启动失败: {ex.Message}", "AltTabReplacer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void RunBackgroundMode()
    {
        // 服务装配
        _ruleEngine = new RuleEngine(_ruleStore!);
        _activator = new WindowActivator();
        _capture = new WindowCaptureService();

        // 宿主窗口
        _hostWindow = new HostWindow();
        _hostWindow.Show();
        var hostHwnd = new WindowInteropHelper(_hostWindow).Handle;
        _enumerator = new WindowEnumerator(_ruleEngine, hostHwnd);

        // 热键服务
        var hwndSource = HwndSource.FromHwnd(hostHwnd);
        _hotkey = new HotkeyService(Dispatcher);
        var ok = _hotkey.Register(hwndSource!.Handle, _settings!.Hotkey.Modifiers, _settings.Hotkey.Key);
        if (!ok)
        {
            System.Windows.MessageBox.Show(
                $"无法注册热键 {_settings.Hotkey.Modifiers} + {_settings.Hotkey.Key}，可能已被其他程序占用。\n\n请修改 %APPDATA%\\AltTabReplacer\\settings.json 换一个组合，然后重新启动。",
                "AltTabReplacer", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.HotkeyReleased += OnHotkeyReleased;
        Logger.Info($"初始化完成，等待热键 {_settings.Hotkey.Modifiers} + {_settings.Hotkey.Key}");

        // 6) 全局低层键盘钩子：仅在选择器显示期间拦截 123/QWE
        // 关键：绕开 IME（中文输入法在应用层拦截键盘事件）
        _llHook = new LowLevelKeyboardHook();
        _llHook.Install(vk =>
        {
            // hook 线程：只能做"原子"判断
            if (!_selectorActive) return false;
            return Core.KeyMap.ToIndex(vk).HasValue;
        });
        _llHook.KeyObserved += vk =>
        {
            // 投回 WPF 线程
            Dispatcher.BeginInvoke(() => _selector?.HandleVk(vk));
        };

        // 7) 托盘图标 + 右键菜单
        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示选择器 (Alt+Z)", null, (_, __) => OnHotkeyPressed());
        menu.Items.Add("打开规则配置", null, (_, __) => LaunchConfigInNewProcess());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, __) =>
        {
            Logger.Info("用户从托盘菜单退出");
            Shutdown();
        });

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "AltTabReplacer — Alt+Z 唤起",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, __) => OnHotkeyPressed();
        Logger.Info("托盘图标已创建");

        // 第一次启动若规则为空，提示用户配置
        if (_ruleStore!.Current.Count == 0)
        {
            var result = System.Windows.MessageBox.Show(
                "尚未配置任何排序规则。\n现在打开规则配置窗口吗？",
                "AltTabReplacer", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                LaunchConfigInNewProcess();
            }
        }
    }

    private void RunConfigMode()
    {
        Logger.Info("进入配置模式");
        var win = new ConfigWindow(_ruleStore!, _rulesPath!);
        // 关掉 ConfigWindow → 整个 app 退出
        win.Closed += (_, __) =>
        {
            Logger.Info("配置窗口关闭，退出应用");
            Shutdown();
        };
        win.Show();
    }

    /// <summary>在独立进程中启动 --config 窗口（不退出当前后台进程）。</summary>
    private void LaunchConfigInNewProcess()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule!.FileName;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--config",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("启动配置窗口失败", ex);
        }
    }

    /// <summary>暴露给 SelectorWindow 复用（避免每个 cell 重新 new）。</summary>
    public WindowCaptureService? CaptureService => _capture;

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _llHook?.Dispose();
        _hotkey?.Dispose();
        _hostWindow?.Close();
        base.OnExit(e);
    }

    // ----------------------------------------------------------
    //  热键响应
    // ----------------------------------------------------------

    private void OnHotkeyPressed()
    {
        try
        {
            // toggle: 第二次按 Alt+Z → 关闭选择器
            if (_selector != null && _selector.IsVisible)
            {
                _selectorActive = false;
                _selector.Cancel();
                return;
            }

            var windows = _enumerator!.Enumerate();
            if (windows.Count == 0)
            {
                Logger.Info("无可见窗口，跳过");
                return;
            }

            _selector = new SelectorWindow(windows, _capture!, _settings!);
            _selector.Closed += (_, __) => _selectorActive = false;
            _selector.OrderChanged += OnSelectorOrderChanged;
            _selectorActive = true;
            _selector.Show();
            Logger.Info($"选择器显示: {windows.Count} 个窗口");
        }
        catch (Exception ex)
        {
            Logger.Error("显示选择器失败", ex);
        }
    }

    /// <summary>
    /// Quick-Switcher 模式：修饰键释放不关闭选择器。
    /// 仅在用户主动操作（点击 / 按数字键 / Esc / 再次按 Alt+Z）时关闭。
    /// </summary>
    private void OnHotkeyReleased()
    {
        // 故意为空：保持选择器显示
    }

    /// <summary>把 SelectorWindow 中的当前顺序写回 RuleStore（用 Priority 数值控制位置）。</summary>
    private void OnSelectorOrderChanged(IReadOnlyList<ViewModels.WindowCellViewModel> cells)
    {
        if (_ruleStore == null || cells.Count == 0) return;
        var newRules = new List<Core.Models.SortRule>(cells.Count);
        // 列表前部 Priority 高，RuleEngine 严格按 Priority 降序排
        for (int i = 0; i < cells.Count; i++)
        {
            newRules.Add(new Core.Models.SortRule
            {
                MatchType = Core.Models.MatchType.ProcessName,
                Pattern = cells[i].Info.ProcessName,
                Priority = (cells.Count - i) * 100,
                IsPinned = false,
                IsExcluded = false,
            });
        }
        _ruleStore.ReplaceProcessNameRules(newRules);
        Logger.Info($"已持久化新顺序: {newRules.Count} 条规则（前部 Priority={newRules[0].Priority}）");
    }
}
