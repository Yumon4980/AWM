using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using MatchType = AltTabReplacer.Core.Models.MatchType;

namespace AltTabReplacer.Core.Services;

/// <summary>规则仓储：JSON 持久化 + FileSystemWatcher 热重载。</summary>
public sealed class RuleStore
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private List<SortRule> _rules = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public IReadOnlyList<SortRule> Current
    {
        get { lock (_lock) return _rules.ToArray(); }
    }

    public event Action? Changed;

    public RuleStore(string path)
    {
        _path = path;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _rules = new List<SortRule>();
                Save();   // 落盘一个空文件
            }
            else
            {
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<List<SortRule>>(json, _opts);
                _rules = parsed ?? new List<SortRule>();
            }
            StartWatcher();
        }
        catch (Exception ex)
        {
            Logger.Error($"规则加载失败，使用空规则: {ex.Message}");
            _rules = new List<SortRule>();
        }
    }

    public void Add(SortRule rule)
    {
        lock (_lock) _rules.Add(rule);
        Save();
    }

    public void Remove(Guid id)
    {
        lock (_lock) _rules.RemoveAll(r => r.Id == id);
        Save();
    }

    public void Update(SortRule rule)
    {
        lock (_lock)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Id == rule.Id) { _rules[i] = rule; break; }
            }
        }
        Save();
    }

    /// <summary>
    /// 清掉所有自动生成过的排序规则。
    /// 布局已经由 <see cref="LayoutStore"/> 接管，旧版写进 rules.json 的
    /// ProcessName 排序规则不再有意义，留着只会干扰排除规则的阅读。
    /// 用户手写的 WindowTitle / Regex 规则和任何 IsExcluded 规则都保留。
    /// </summary>
    public int PurgeLegacyOrderRules()
    {
        int removed;
        lock (_lock)
        {
            removed = _rules.RemoveAll(r => !r.IsExcluded && r.MatchType == MatchType.ProcessName);
        }
        if (removed > 0) Save();
        return removed;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            lock (_lock)
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_rules, _opts));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"规则保存失败: {ex.Message}");
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
            Logger.Warn($"启动 FileSystemWatcher 失败: {ex.Message}");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 防抖 500ms
        _debounce?.Dispose();
        _debounce = new Timer(_ =>
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var parsed = JsonSerializer.Deserialize<List<SortRule>>(json, _opts) ?? new List<SortRule>();
                lock (_lock) _rules = parsed;
                Changed?.Invoke();
                Logger.Info($"规则热重载: {_rules.Count} 条");
            }
            catch (Exception ex)
            {
                Logger.Error($"规则热重载失败: {ex.Message}");
            }
        }, null, 500, Timeout.Infinite);
    }
}
