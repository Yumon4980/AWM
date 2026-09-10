using System;

namespace AltTabReplacer.Core.Models;

/// <summary>
/// 一条排序/置顶/排除规则。
/// </summary>
public sealed record SortRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MatchType MatchType { get; init; } = MatchType.ProcessName;
    public string Pattern { get; init; } = "";
    public int Priority { get; init; } = 0;
    public bool IsPinned { get; init; } = false;
    public bool IsExcluded { get; init; } = false;
}
