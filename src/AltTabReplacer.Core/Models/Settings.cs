using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltTabReplacer.Core.Models;

/// <summary>
/// 用户可配置项。从 %APPDATA%\AltTabReplacer\settings.json 加载。
/// 加载失败时退回到 <see cref="Defaults"/>。
/// </summary>
public sealed class Settings
{
    public HotkeyConfig Hotkey { get; set; } = new();
    public LayoutConfig Layout { get; set; } = new();
    public ThemeConfig Theme { get; set; } = new();
    public BehaviorConfig Behavior { get; set; } = new();

    public static Settings Defaults => new();

    // ----------------------------------------------------------

    public sealed class HotkeyConfig
    {
        public string Modifiers { get; set; } = "Ctrl, Alt";   // 修饰键，逗号分隔
        public string Key { get; set; } = "Tab";              // 主键（VK 名）
    }

    public sealed class LayoutConfig
    {
        public int CellWidth { get; set; } = 256;
        public int CellHeight { get; set; } = 144;
        public int CellPadding { get; set; } = 8;
        public int MaxColumns { get; set; } = 10;
        public int MaxPerPage { get; set; } = 35;
    }

    public sealed class ThemeConfig
    {
        public string Accent { get; set; } = "#FF0078D4";
        public string Background { get; set; } = "#CC202020";
        public int CornerRadius { get; set; } = 8;
    }

    public sealed class BehaviorConfig
    {
        public bool HideOnWindowChange { get; set; } = true;
        public bool IgnoreFullscreen { get; set; } = true;
        public int PollIntervalMs { get; set; } = 1000;
    }
}

/// <summary>JSON 读写辅助。</summary>
public static class SettingsLoader
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "AltTabReplacer",
        "settings.json");

    public static Settings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var defaults = Settings.Defaults;
                Save(path, defaults);
                return defaults;
            }
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(json, _opts);
            return s ?? Settings.Defaults;
        }
        catch (System.Exception ex)
        {
            Infrastructure.Logger.Error($"Settings 加载失败，使用默认值: {ex.Message}");
            return Settings.Defaults;
        }
    }

    public static void Save(string path, Settings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, _opts));
    }
}
