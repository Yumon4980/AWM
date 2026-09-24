using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// "切换后聚焦输入框"配置的持久化：`%APPDATA%\AltTabReplacer\focus_targets.json`。
/// 键是进程名（不带 .exe），一个进程最多一条——再录一次即覆盖。
/// 量级是个位数到几十条，直接整文件读写，不做 watcher / 防抖。
/// </summary>
public sealed class FocusTargetStore
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    private readonly string _path;
    private readonly object _lock = new();
    private FocusTargetDocument _doc = new();

    public FocusTargetStore(string path) => _path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AltTabReplacer", "focus_targets.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Save();
                return;
            }
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<FocusTargetDocument>(json, _opts);
            lock (_lock) _doc = doc ?? new FocusTargetDocument();
            Logger.Info($"聚焦配置加载完成: {Snapshot().Count} 条");
        }
        catch (Exception ex)
        {
            Logger.Error($"聚焦配置加载失败，使用空配置: {ex.Message}");
            lock (_lock) _doc = new FocusTargetDocument();
        }
    }

    /// <summary>快照（列表副本）。UI 绑定 / 查找都走它，避免边遍历边改。</summary>
    public System.Collections.Generic.List<FocusTargetEntry> Snapshot()
    {
        lock (_lock) return _doc.Targets.ToList();
    }

    public FocusTargetEntry? Find(string processName)
    {
        var key = FocusTargetService.NormalizeProcess(processName);
        lock (_lock) return _doc.Targets.FirstOrDefault(t =>
            string.Equals(t.Process, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按进程名覆盖式写入并落盘。</summary>
    public void Upsert(FocusTargetEntry entry)
    {
        lock (_lock)
        {
            _doc.Targets.RemoveAll(t =>
                string.Equals(t.Process, entry.Process, StringComparison.OrdinalIgnoreCase));
            _doc.Targets.Add(entry);
            Save();
        }
    }

    /// <summary>按进程名删除并落盘。返回是否真的删了。</summary>
    public bool Remove(string processName)
    {
        var key = FocusTargetService.NormalizeProcess(processName);
        lock (_lock)
        {
            int n = _doc.Targets.RemoveAll(t =>
                string.Equals(t.Process, key, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Save();
            return n > 0;
        }
    }

    /// <summary>调用方需持有 _lock（Upsert/Remove 内部落盘，避免重复加锁）。</summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            FocusTargetDocument doc;
            lock (_lock) doc = _doc;
            File.WriteAllText(_path, JsonSerializer.Serialize(doc, _opts));
        }
        catch (Exception ex)
        {
            Logger.Error($"聚焦配置保存失败: {ex.Message}");
        }
    }
}
