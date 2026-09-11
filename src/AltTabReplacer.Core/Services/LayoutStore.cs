using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 槽位布局的持久化：`%APPDATA%\AltTabReplacer\layout.json`。
///
/// 这是**布局的唯一真相来源**（槽位顺序 + 分组关系）。
/// `rules.json` / <see cref="RuleStore"/> 退化为只管排除规则，不再承担排序职责。
/// </summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private LayoutDocument _doc = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    /// <summary>自己写盘时置位，避免 watcher 把自己的写入当成外部改动再回灌一次。</summary>
    private DateTime _selfWriteAt = DateTime.MinValue;

    public LayoutDocument Current
    {
        get { lock (_lock) return _doc; }
    }

    public event Action? Changed;

    public LayoutStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AltTabReplacer", "layout.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _doc = new LayoutDocument();
                Save(_doc);
            }
            else
            {
                var json = File.ReadAllText(_path);
                _doc = JsonSerializer.Deserialize<LayoutDocument>(json, _opts) ?? new LayoutDocument();
            }
            Logger.Info($"布局加载完成: {_doc.Slots.Count} 个槽位");
            StartWatcher();
        }
        catch (Exception ex)
        {
            Logger.Error($"布局加载失败，使用空布局: {ex.Message}");
            _doc = new LayoutDocument();
        }
    }

    public void Save(LayoutDocument doc)
    {
        lock (_lock) _doc = doc;
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_lock)
            {
                _selfWriteAt = DateTime.UtcNow;
                File.WriteAllText(_path, JsonSerializer.Serialize(doc, _opts));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"布局保存失败: {ex.Message}");
        }
    }

    private void StartWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            var name = Path.GetFileName(_path);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;

            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn($"布局 FileSystemWatcher 启动失败: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            try
            {
                // 自己刚写过就跳过，否则每次拖动都会触发一次无意义的回灌
                lock (_lock)
                {
                    if ((DateTime.UtcNow - _selfWriteAt).TotalMilliseconds < 1500) return;
                }
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<LayoutDocument>(json, _opts) ?? new LayoutDocument();
                lock (_lock) _doc = parsed;
                Changed?.Invoke();
                Logger.Info($"布局热重载: {parsed.Slots.Count} 个槽位");
            }
            catch (Exception ex)
            {
                Logger.Error($"布局热重载失败: {ex.Message}");
            }
        }, null, 500, Timeout.Infinite);
    }
}
