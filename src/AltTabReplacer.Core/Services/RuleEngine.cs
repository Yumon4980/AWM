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
/// 只负责**排除**。
///
/// 排序职责已经整个移交给 <see cref="LayoutStore"/> + <see cref="LayoutResolver"/>。
/// 旧版把顺序编码进 <see cref="SortRule.Priority"/> 的做法有个绕不过去的坑：
/// 一个进程开多个窗口时会写出多条同 Pattern、不同 Priority 的规则，
/// 而"取命中规则的最大值"会把该进程的每一个窗口都抬到它占过的最高位置，
/// 顺序看起来就像没被记住。现在顺序由布局树显式表达，不再有这个歧义。
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

        var excluders = rules.Where(r => r.IsExcluded).ToList();
        if (excluders.Count == 0) return raw;

        return raw.Where(w => !excluders.Any(r => Match(r, w))).ToList();
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
