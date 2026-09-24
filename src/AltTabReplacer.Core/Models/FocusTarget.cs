using System.Collections.Generic;

namespace AltTabReplacer.Core.Models;

/// <summary>
/// 一个"切换到该程序后要聚焦的输入框"的录制条目。
///
/// 匹配线索按稳定性排序：
///   AutomationId —— 开发者给控件起的稳定 ID（浏览器地址栏、WPF/WinForms 控件都有），优先用；
///   Name         —— 控件的可见文本/标签。对"内容即名字"的编辑框（记事本正文）会随内容漂移，只做次选；
///   ControlTypeId —— 兜底：该类型的第一个控件（Edit），线索全失效时仍能落到"最像的输入框"。
/// </summary>
public sealed class FocusTargetEntry
{
    /// <summary>进程名（不带 .exe），是本条目的键：一个进程录一个输入框。</summary>
    public string Process { get; set; } = "";

    /// <summary>UIA AutomationId。null = 捕获时没有。</summary>
    public string? AutomationId { get; set; }

    /// <summary>UIA Name。null = 捕获时没有。</summary>
    public string? Name { get; set; }

    /// <summary>UIA ControlType 的数字 Id（如 Edit=50004）。0 = 未知。</summary>
    public int ControlTypeId { get; set; }

    /// <summary>Win32 类名（如 "Edit"）。匿名叶子的最后一条强线索。</summary>
    public string? ClassName { get; set; }

    /// <summary>录制时间（本地），仅展示用。</summary>
    public string? CapturedAt { get; set; }
}

/// <summary>`focus_targets.json` 的根对象。</summary>
public sealed class FocusTargetDocument
{
    public int Version { get; set; } = 1;
    public List<FocusTargetEntry> Targets { get; set; } = new();
}
