using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

public interface IRuleEngine
{
    IReadOnlyList<WindowInfo> Apply(IReadOnlyList<WindowInfo> raw);
}

/// <summary>
/// 应用置顶 / 排除 / 优先级规则。
///
/// 应用顺序：
///   1) 排除（命中 IsExcluded 的窗口直接丢弃）
///   2) 置顶（命中 IsPinned 的窗口移到最前，组内保持原 z-order）
///   3) Priority 降序排序（同 Priority 保持 z-order 倒序）
/// </summary>
public sealed class RuleEngine : IRuleEngine
{
    private readonly RuleStore _store;

    public RuleEngine(RuleStore store)
    {
        _store = store;
    }

    public IReadOnlyList<WindowInfo> Apply(IReadOnlyList<WindowInfo> raw)
    {
        var rules = _store.Current;
        if (rules.Count == 0) return raw;

        // 1) 排除
        var filtered = raw.Where(w => !rules.Any(r => r.IsExcluded && Match(r, w))).ToList();
        if (filtered.Count == 0) return filtered;

        // 2) 每个窗口的 effective priority = 命中规则中的最大 Priority
        //    （不依赖 IsPinned：纯 Priority 数值排序，方便运行时改写）
        int PriorityOf(WindowInfo w)
        {
            var hits = rules.Where(r => !r.IsExcluded && Match(r, w)).Select(r => r.Priority);
            return hits.Any() ? hits.Max() : 0;
        }

        // 3) 按 effective priority 降序；同 priority 保持 z-order 倒序
        var sorted = filtered
            .Select((w, i) => (Window: w, OriginalIndex: i, Pri: PriorityOf(w)))
            .OrderByDescending(x => x.Pri)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Window)
            .ToList();

        return sorted;
    }

    private static bool Match(SortRule rule, WindowInfo w)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern)) return false;
        try
        {
            return rule.MatchType switch
            {
                MatchType.ProcessName => string.Equals(w.ProcessName, rule.Pattern, StringComparison.OrdinalIgnoreCase),
                MatchType.WindowTitle => w.Title.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase),
                MatchType.Regex => Regex.IsMatch(w.Title, rule.Pattern, RegexOptions.IgnoreCase),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}
