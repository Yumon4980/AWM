using System;
using System.Windows;
using AltTabReplacer.Core.Models;
using Microsoft.Win32;

namespace AltTabReplacer;

/// <summary>
/// UI 主题切换：根据 <see cref="ThemeMode"/> 合并对应的资源字典到
/// <see cref="Application.Resources"/>；<see cref="ThemeMode.System"/> 时跟随
/// Windows 当前主题（通过 <see cref="SystemEvents.UserPreferenceChanged"/> 监听）。
///
/// 本期只覆盖 SettingsWindow 自身——主界面（SelectorWindow）和 ConfigWindow
/// 仍是深色，原因是它们的 XAML 里写死了大量 #FFxxxxxx，迁移到 DynamicResource
/// 是独立工作。
/// </summary>
public static class ThemeManager
{
    private const string DarkDictUri  = "pack://application:,,,/Themes/Dark.xaml";
    private const string LightDictUri = "pack://application:,,,/Themes/Light.xaml";

    private static bool _initialized;
    /// <summary>系统主题最后一次被探测到的实际值（避免和 System 重复计算）。</summary>
    private static bool _systemIsLight;

    /// <summary>当前生效的实际主题（System 模式会被解析为 Light 或 Dark）。</summary>
    public static ThemeMode Effective { get; private set; } = ThemeMode.Dark;

    /// <summary>模式变化或系统主题变化时触发，参数是解析后的实际模式。</summary>
    public static event Action<ThemeMode>? Changed;

    /// <summary>
    /// 初始化系统主题监听、记录初始系统值。
    /// 必须在 WPF Application 创建后才能调用（依赖 SystemEvents 静态 API）。
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        _systemIsLight = QuerySystemIsLight();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// 按指定模式应用主题。重复调用是幂等的：会先摘掉旧的 theme 字典再合并新的。
    /// </summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        ThemeMode actual = mode == ThemeMode.System ? (_systemIsLight ? ThemeMode.Light : ThemeMode.Dark) : mode;
        if (actual == Effective) return;        // 已经是这个主题，跳过（避免无意义的资源字典替换）
        Effective = actual;

        var app = System.Windows.Application.Current;
        if (app == null) return;

        // 1) 移除旧的 theme 字典。两条 MergedDictionaries 都会被处理。
        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source;
            if (src != null && (src.ToString().EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)
                             || src.ToString().EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                merged.RemoveAt(i);
            }
        }

        // 2) 合并新的。注意：XAML 里 UriKind.Relative 表示相对于 XAML 文件所在目录，
        //    这两文件就在项目根，运行后位于 exe 同目录，能解析到。
        var dict = new ResourceDictionary
        {
            Source = new Uri(actual == ThemeMode.Light ? LightDictUri : DarkDictUri, UriKind.Absolute),
        };
        merged.Add(dict);

        Changed?.Invoke(actual);
    }

    /// <summary>关闭时清理订阅，避免在 app 关闭路径上 SystemEvents 仍持有委托。</summary>
    public static void Shutdown()
    {
        if (!_initialized) return;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _initialized = false;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        _systemIsLight = QuerySystemIsLight();
        // System 模式下重应用一次，让 SettingsWindow 等已经显示的窗口立刻跟随。
        // ApplyTheme 内部 Effective 比较保证非 System 模式不会被误刷新。
        ApplyTheme(ThemeMode.System);
    }

    /// <summary>
    /// 探测 Windows 当前主题。优先用 UWP UISettings.GetColorValues（Windows 10 1903+），
    /// 失败时回退到读注册表 AppsUseLightTheme。
    /// </summary>
    private static bool QuerySystemIsLight()
    {
        try
        {
            // 注册表路径在 LocalMachine；HKCU 也存在但 HKLM 在多用户下更可靠。
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                var v = key.GetValue("AppsUseLightTheme");
                if (v is int i) return i != 0;
            }
        }
        catch
        {
            // 注册表读失败（权限 / 不存在），按深色处理——选择器本身一直是深色
        }
        return false;
    }
}