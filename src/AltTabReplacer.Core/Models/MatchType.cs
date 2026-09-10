namespace AltTabReplacer.Core.Models;

/// <summary>排序规则匹配维度。</summary>
public enum MatchType
{
    /// <summary>精确匹配进程文件名（不区分大小写）。</summary>
    ProcessName = 0,

    /// <summary>窗口标题 Contains（不区分大小写）。</summary>
    WindowTitle = 1,

    /// <summary>完整正则（不区分大小写）。</summary>
    Regex = 2,
}
