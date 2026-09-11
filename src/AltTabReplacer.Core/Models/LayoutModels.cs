using System;
using System.Collections.Generic;

namespace AltTabReplacer.Core.Models;

/// <summary>槽位类型。</summary>
public enum SlotKind
{
    /// <summary>叶子：一个具体窗口，按下直接切过去。</summary>
    Window = 0,

    /// <summary>组：按下后进入二级，再选具体窗口。</summary>
    Group = 1,
}

/// <summary>
/// 跨会话识别窗口的匹配器。
///
/// **只锚定在 ProcessName 上**，这是唯一真正稳定的线索：
/// HWND 重启即变；窗口标题会随着 VS Code 换文件、浏览器换标签而变。
/// 窗口级的易变性一律交给运行时的 z-order 吸收，不做持久化。
/// </summary>
public sealed record WindowMatcher(string ProcessName)
{
    public bool Matches(WindowInfo w) =>
        string.Equals(w.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 持久化的槽位定义。运行时会被 <see cref="Services.LayoutResolver"/> 解析成实际的槽位树。
/// </summary>
public sealed class SlotDefinition
{
    public SlotKind Kind { get; set; } = SlotKind.Window;

    /// <summary>组名。Kind=Window 时忽略。空表示自动生成。</summary>
    public string? Name { get; set; }

    /// <summary>
    /// 本槽位包含的进程。
    /// Kind=Window 时只用第一项；Kind=Group 时是组成员（可跨进程）。
    /// </summary>
    public List<string> Processes { get; set; } = new();
}

/// <summary>`layout.json` 的根对象。</summary>
public sealed class LayoutDocument
{
    public int Version { get; set; } = 1;
    public List<SlotDefinition> Slots { get; set; } = new();
}
