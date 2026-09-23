using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AltTabReplacer.Core;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using AltTabReplacer.Core.Services;
using WinForms = System.Windows.Forms;

namespace AltTabReplacer;

/// <summary>
/// 应用入口。
/// 两种运行模式：
///   1) 默认（无参数）：后台常驻 + 接管 Alt+Tab（替代系统任务切换器）
///   2) --config：打开规则配置窗口（前台模式，不监听热键）
/// </summary>
public partial class App : System.Windows.Application
{
    private Settings? _settings;
    private RuleStore? _ruleStore;
    private RuleEngine? _ruleEngine;
    private LayoutStore? _layoutStore;
    private WindowEnumerator? _enumerator;
    private WindowActivator? _activator;
    private WindowCaptureService? _capture;
    private HotkeyService? _hotkey;
    private HostWindow? _hostWindow;
    private SelectorWindow? _selector;
    private LowLevelKeyboardHook? _llHook;
    private DispatcherTimer? _hookWatchdog;
    private bool _hookWasInstalled = true;
    private WinForms.NotifyIcon? _trayIcon;
    /// <summary>托盘菜单里“缩略图”项的引用。运行时需要更新文字反映当前状态。</summary>
    private WinForms.ToolStripMenuItem? _thumbnailsMenuItem;
    private volatile bool _selectorActive;
    /// <summary>
    /// 暂时放行索引键（16 个键）给文本输入。
    /// 搜索模式和重命名对话框都要用——否则输入框里打 123qwe 会被钩子吞掉，一个字都打不进去。
    /// </summary>
    private volatile bool _suspendIndexCapture;
    private string? _rulesPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 常驻托盘程序最糟的失败方式是"悄悄消失"。UI 线程上任何漏网的异常
        // 都会直接终止进程，所以统一记日志并吞掉——留一条可查的痕迹，总好过让用户
        // 面对一个无缘无故不见了的热键。
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("未处理的 UI 异常（已拦截，程序继续运行）", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("未处理的域异常", ex);
        };

        try
        {
            Logger.Info("=== AltTabReplacer 启动 ===");

            // 1) 解析命令行
            bool configMode = e.Args.Any(a => string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase));

            // 1b) 加载键位网格配置（必须在任何用到 KeyMap.Size / Cols / LabelOf 的代码之前）。
            //     从 exe 同目录的 config.json 读，文件不存在 / 解析失败退回默认 4×4 QWERTY。
            var keyMapPath = KeyMapConfig.DefaultPath;
            KeyMap.Configure(KeyMapConfig.Load(keyMapPath));

            // 2) 加载配置
            _settings = SettingsLoader.Load(SettingsLoader.DefaultPath);
            Logger.Info($"配置加载完成: {SettingsLoader.DefaultPath}");

            // 3) 加载规则
            _rulesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AltTabReplacer", "rules.json");
            _ruleStore = new RuleStore(_rulesPath);
            _ruleStore.Load();

            // 3b) 旧版把顺序编码进 rules.json 的 Priority，现在顺序由 layout.json 接管，
            //     残留的排序规则只会干扰排除规则的阅读，启动时清掉一次
            int purged = _ruleStore.PurgeLegacyOrderRules();
            if (purged > 0) Logger.Info($"已清理 {purged} 条旧版排序规则（顺序改由 layout.json 管理）");

            // 4) 加载槽位布局
            _layoutStore = new LayoutStore(LayoutStore.DefaultPath);
            _layoutStore.Load();

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
        TryRaiseProcessPriority();
        LogElevationState();

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

        // 6) 全局低层键盘钩子，两个职责：
        //    a) 吞掉 Alt+Tab —— 系统保留组合，RegisterHotKey 注册不到，只有在这里截下来
        //       系统任务切换器才不会弹出（即"替换系统 Alt+Tab"）
        //    b) 选择器显示期间截 16 个索引键，绕开 IME（中文输入法在应用层拦截键盘事件）
        _llHook = new LowLevelKeyboardHook();
        bool hookOk = _llHook.Install(e =>
        {
            // hook 线程：只能做"原子"判断
            if (_hotkey!.TryHandleHookKey(e)) return LowLevelKeyboardHook.HookAction.Swallow;
            if (!_selectorActive || !e.IsDown) return LowLevelKeyboardHook.HookAction.Pass;
            // 搜索模式 / 重命名对话框期间，索引键要留给文本输入，不能再吞
            if (_suspendIndexCapture) return LowLevelKeyboardHook.HookAction.Pass;
            // Ctrl 按着时不吞索引键：把 Ctrl+1/2/3/4 这类快捷键让给系统 / 前台程序
            if (e.Ctrl) return LowLevelKeyboardHook.HookAction.Pass;
            return Core.KeyMap.ToIndex(e.Vk).HasValue
                ? LowLevelKeyboardHook.HookAction.SwallowAndObserve
                : LowLevelKeyboardHook.HookAction.Pass;
        });
        _llHook.KeyObserved += e =>
        {
            // 投回 WPF 线程
            Dispatcher.BeginInvoke(() => _selector?.HandleVk(e.Vk));
        };

        if (!hookOk && _hotkey.IsHookMode)
        {
            System.Windows.MessageBox.Show(
                $"无法安装全局键盘钩子，{HotkeyLabel} 不会被接管（系统自带的任务切换器仍会弹出）。\n\n" +
                "请检查是否有安全软件拦截，或以管理员身份重新运行。",
                "AltTabReplacer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StartHookWatchdog();

        // 7) 托盘图标 + 右键菜单
        SetupTrayIcon();
    }

    /// <summary>
    /// 定期重挂低层键盘钩子，把自己顶回钩子链头部。
    /// 钩子链是"后装的先调用"，任何后启动、也挂了键盘钩子的程序都会排在我们前面并可能吞掉 Alt+Tab；
    /// 重挂一次即可抢回。顺带也能从"被系统静默摘钩"里自愈。
    ///
    /// 重挂的瞬间有个极短空窗期，这期间的 Alt+Tab 会漏给系统，所以按着修饰键时跳过这一轮。
    /// </summary>
    private void StartHookWatchdog()
    {
        _hookWatchdog = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        _hookWatchdog.Tick += (_, __) =>
        {
            if (_llHook == null) return;

            // 掉钩子是"静默"的：没有任何回调或通知，只能靠状态自己发现
            bool installed = _llHook.IsInstalled;
            if (installed != _hookWasInstalled)
            {
                _hookWasInstalled = installed;
                if (installed) Logger.Info("键盘钩子已恢复");
                else Logger.Error($"键盘钩子丢失！Alt+Tab 此刻无法接管 (err={_llHook.LastInstallError})");
            }

            if (AnyModifierDown()) return;      // 别在用户正按着 Alt 的时候拆钩子
            _llHook.Reinstall();
        };
        _hookWatchdog.Start();
    }

    private static bool AnyModifierDown()
    {
        // VK_SHIFT / VK_CONTROL / VK_MENU / VK_LWIN / VK_RWIN
        foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// 低层键盘钩子的回调必须在 LowLevelHooksTimeout（默认 300ms）内返回，否则系统静默摘钩。
    /// 被别的进程抢 CPU 是最常见的超时原因，所以把自己的优先级抬一档。
    /// </summary>
    private static void TryRaiseProcessPriority()
    {
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            Logger.Info("进程优先级已提升到 AboveNormal");
        }
        catch (Exception ex)
        {
            Logger.Warn($"提升进程优先级失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 非管理员身份运行时，低层键盘钩子收不到发往**更高完整性级别**窗口的按键
    /// （任务管理器、以管理员身份运行的程序、UAC 安全桌面等）。
    /// 这类窗口在前台时 Alt+Tab 会漏给系统，弹出来的是系统自带的切换器——
    /// 这是"在某些程序里热键失灵"最常见的原因，且**只能靠本程序也提权**来解决。
    /// </summary>
    private static void LogElevationState()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Logger.Info("以管理员身份运行：提权窗口在前台时热键依然有效");
            }
            else
            {
                Logger.Warn("非管理员身份运行：提权窗口（任务管理器等）在前台时 Alt+Tab 无法接管");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"检测提权状态失败: {ex.Message}");
        }
    }

    /// <summary>供 UI 文案使用的热键显示名，例如 "Alt+Tab"。</summary>
    private string HotkeyLabel =>
        $"{_settings!.Hotkey.Modifiers}+{_settings.Hotkey.Key}".Replace(" ", "").Replace(",", "+");

    private void SetupTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add($"显示选择器 ({HotkeyLabel})", null, (_, __) => OnHotkeyPressed());
        // menu.Items.Add("重置分组与顺序", null, (_, __) => ResetLayout());#已屏蔽，后续考虑加不加入
        // menu.Items.Add("打开规则配置", null, (_, __) => LaunchConfigInNewProcess());#已屏蔽，后续考虑加不加入

        _thumbnailsMenuItem = new WinForms.ToolStripMenuItem("缩略图：—")
        {
            CheckOnClick = true,
            Checked = _settings!.Behavior.ShowThumbnails,
        };
        _thumbnailsMenuItem.CheckedChanged += (_, __) => OnToggleThumbnails(_thumbnailsMenuItem.Checked);
        UpdateThumbnailsMenuLabel();      // 初始化"开 / 关"文字
        menu.Items.Add(_thumbnailsMenuItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, __) =>
        {
            Logger.Info("用户从托盘菜单退出");
            Shutdown();
        });

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = $"AltTabReplacer — {HotkeyLabel} 唤起",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, __) => OnHotkeyPressed();
        Logger.Info("托盘图标已创建");

        // 规则为空不再弹窗——托盘菜单里随时可以打开配置
    }

    /// <summary>同步托盘菜单上"缩略图"项的文字，反映当前状态。</summary>
    private void UpdateThumbnailsMenuLabel()
    {
        if (_thumbnailsMenuItem == null || _settings == null) return;
        _thumbnailsMenuItem.Text = _settings.Behavior.ShowThumbnails ? "缩略图：开" : "缩略图：关";
    }

    /// <summary>
    /// 托盘菜单里切换了缩略图：写回设置、刷菜单文字。
    /// 如果选择器当前开着，立即刷新预览区（不重启选择器）。
    /// </summary>
    private void OnToggleThumbnails(bool enabled)
    {
        if (_settings == null) return;
        if (_settings.Behavior.ShowThumbnails == enabled) return;

        _settings.Behavior.ShowThumbnails = enabled;
        try
        {
            SettingsLoader.Save(SettingsLoader.DefaultPath, _settings);
        }
        catch (Exception ex)
        {
            Logger.Error($"保存缩略图设置失败: {ex.Message}");
        }
        UpdateThumbnailsMenuLabel();
        Logger.Info($"缩略图已{(enabled ? "开启" : "关闭")}");

        // 已开着的选择器：立即用新设置重画预览。不开着的下次唤起自然用新设置。
        _selector?.RefreshPreview();
    }

    /// <summary>取 exe 上嵌入的图标当托盘图标；失败退回系统默认图标。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico != null) return ico;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"加载程序图标失败: {ex.Message}");
        }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>清空 layout.json，回到"全部按 z-order + 自动折叠"的初始状态。</summary>
    private void ResetLayout()
    {
        var r = System.Windows.MessageBox.Show(
            "将清空所有手动分组和拖动排序，回到按最近使用顺序自动排列。\n\n确定吗？",
            "AltTabReplacer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        _layoutStore?.Save(new Core.Models.LayoutDocument());
        Logger.Info("用户重置了分组与顺序");
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
        _hookWatchdog?.Stop();
        _hookWatchdog = null;
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
            // 选择器已打开时再按热键 = 关闭，和 Esc 完全一致
            if (_selector != null && _selector.IsVisible)
            {
                Logger.Info("热键再次按下，关闭选择器");
                _selectorActive = false;
                _selector.Cancel();
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var windows = _enumerator!.Enumerate();

            // 枚举结果 + 持久化布局 → 实际的槽位树（含自动折叠）。
            // 一页放不下的槽位不再折成"更多…"，由选择器放进"未入网格"列表。
            // 窗口全关时也照常解析：锁定槽位会以"未运行"占位出现，按键可重新启动。
            var slots = LayoutResolver.Resolve(
                windows,
                _layoutStore!.Current,
                _settings!.Layout.AutoGroupThreshold);

            if (windows.Count == 0 && slots.Count == 0)
            {
                Logger.Info("无可见窗口且没有锁定槽位，跳过");
                return;
            }

            // 不在这里抢前台。HostWindow 是 Visibility=Hidden 的 0x0 窗口，把它顶到前台
            // 既要 SW_SHOW 一个本该隐藏的窗口，又会把已打开的选择器挤失焦触发自动关闭。
            // 抢前台统一放在 Show() 之后、直接作用在选择器上（AttachThreadInput 本来就不依赖
            // 本进程当前是不是前台）。
            _selector = new SelectorWindow(windows, slots, _capture!, _settings!);
            _selector.Closed += (_, __) => { _selectorActive = false; _suspendIndexCapture = false; };
            _selector.LayoutChanged += OnSelectorLayoutChanged;
            _selector.SuspendIndexCaptureChanged += on => _suspendIndexCapture = on;
            _selectorActive = true;
            _selector.Show();

            // Show() 之后再强制一次：窗口句柄这时才存在，而且导航键全靠键盘焦点
            WindowActivator.ForceForeground(new WindowInteropHelper(_selector).Handle);

            // 耗时要盯着：这条路径上要截图，很容易几百毫秒。
            // 钩子已经挪到专用线程，UI 卡顿不会再摘掉钩子，但太慢依然会让人觉得"按了没反应"。
            long ms = sw.ElapsedMilliseconds;
            int groups = slots.Count(s => s.Kind == Core.Models.SlotKind.Group);
            if (ms > 300) Logger.Warn($"选择器显示: {windows.Count} 窗口 / {slots.Count} 槽位（{groups} 组），耗时 {ms}ms（偏慢）");
            else Logger.Info($"选择器显示: {windows.Count} 窗口 / {slots.Count} 槽位（{groups} 组），耗时 {ms}ms");
        }
        catch (Exception ex)
        {
            Logger.Error("显示选择器失败", ex);
        }
    }

    /// <summary>
    /// Quick-Switcher 模式：修饰键释放不关闭选择器。
    /// 仅在用户主动操作（点击 / 按索引键 / Enter / Esc / 再次按热键）时关闭。
    /// </summary>
    private void OnHotkeyReleased()
    {
        // 故意为空：保持选择器显示
    }
    /// <summary>
    /// 拖动产生的新布局（分组 / 排序）写回 layout.json。
    /// 溢出组会在 ToDocument 里被摊平，不会被固化成一个真的组。
    /// </summary>
    private void OnSelectorLayoutChanged(IReadOnlyList<ResolvedSlot> slots)
    {
        if (_layoutStore == null || slots.Count == 0) return;
        var doc = LayoutResolver.ToDocument(slots);
        _layoutStore.Save(doc);
        Logger.Info($"已持久化布局: {doc.Slots.Count} 个槽位");
    }
}
