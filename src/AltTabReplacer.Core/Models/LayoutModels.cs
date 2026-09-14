using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AltTabReplacer.Core.Models;

/// <summary>槽位类型。</summary>
public enum SlotKind
{
    /// <summary>程序：一个具体窗口，按下直接切过去。</summary>
    Window = 0,

    /// <summary>程序组：按下进入二级，里面装程序 / 程序组合。</summary>
    Group = 1,

    /// <summary>程序组合：按下同时/依次打开组内全部程序（已开则激活，未开则启动）。</summary>
    Combination = 2,
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
/// 精确到**窗口**的成员描述。
///
/// 为什么不能只用进程名：一个进程常开多个窗口（VS Code 5 个），
/// 只记进程名的话，把一个 VS Code 窗口拖进分组会把**全部** VS Code 窗口都拽进去。
/// 所以手工建的分组记的是具体窗口：进程名 + 标题提示。
/// </summary>
public sealed class MemberSpec
{
    public string Process { get; set; } = "";

    /// <summary>窗口标题提示。null = 该进程的任意窗口。</summary>
    public string? Title { get; set; }

    /// <summary>
    /// 用户右键改过的成员显示名。null = 用窗口标题。
    /// 二级界面里"重命名程序"改的就是它，持久化后跟着 Members 走。
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 程序可执行文件路径。程序组合里用来"未开则启动"。
    /// 从运行中的窗口进程反查得到；窗口关掉后这条记录还在，所以能再拉起来。
    /// </summary>
    public string? ExePath { get; set; }

    /// <summary>
    /// 锁定：组内这个成员固定占住当前键位，组内排序 / 移出 / 解散时都不会动它。
    /// 仅对自动折叠组生效——手工组的子项锁定走 <see cref="SlotDefinition.Locked"/> / <see cref="SlotDefinition.Position"/>。
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 窗口句柄提示。**只在内存里用，不落盘**（<see cref="JsonIgnoreAttribute"/>）：
    /// 重启后句柄会被系统回收重用，落盘的旧值可能张冠李戴，所以宁可重新推导。
    ///
    /// 为什么需要它：VS Code / 浏览器这类窗口标题会随文件、标签页变化，
    /// 只按 (进程, 标题) 匹配的话标题一变就认不出原窗口，兜底逻辑会把**新开的**
    /// 同进程窗口抓进组合、把原成员挤出去。有了句柄提示，同一次运行内可以精确找回
    /// 原窗口，新窗口就不会被误吸进组合。首次解析 / 重启后没有提示时，
    /// 退回 (进程, 标题) 与"最长公共后缀"匹配，也能正确落位。
    /// </summary>
    [JsonIgnore]
    public long? Hwnd { get; set; }

    public MemberSpec() { }
    public MemberSpec(string process, string? title)
    {
        Process = process;
        Title = title;
    }

    /// <summary>标题对不上时的兜底：只按进程认领一个窗口。</summary>
    public static MemberSpec AnyOf(string process) => new(process, null);
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
    /// 本槽位涉及的进程。自动折叠的组靠它表达"这个进程的全部窗口"。
    /// </summary>
    public List<string> Processes { get; set; } = new();

    /// <summary>
    /// 精确到窗口的成员。手工建的分组 / 解散后的槽位用它。
    /// null 表示按 <see cref="Processes"/> 取该进程的全部窗口（自动折叠语义）。
    /// </summary>
    public List<MemberSpec>? Members { get; set; }

    /// <summary>
    /// 组内成员的显示顺序（按窗口标题）。
    /// 只在**自动折叠的组**被用户拖过时使用——手工组的顺序由 <see cref="Members"/> 决定。
    /// 这是"提示"而非硬绑定：标题变了就退回 z-order，不会因此丢窗口。
    /// </summary>
    public List<string>? MemberOrder { get; set; }

    /// <summary>
    /// 程序组的子项（程序 / 程序组合）。null = 自动折叠组（按 <see cref="Processes"/> 语义）。
    /// 非 null（哪怕是空列表）= 手工创建的程序组，空组也要保留在界面上。
    /// 层级严格两级：子项里不会再出现程序组。
    /// </summary>
    public List<SlotDefinition>? Children { get; set; }

    /// <summary>
    /// 旧字段：解散单进程组时置位。现已由 <see cref="Members"/> 取代，
    /// 保留只为兼容已经落盘的旧布局。
    /// </summary>
    public bool NoAutoGroup { get; set; }

    /// <summary>
    /// 锁定：这个槽位固定占住 <see cref="Position"/> 键位，排序 / 删除都不会把它挤走。
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// 显式键位索引（0..15）。null = 自动（按列表顺序往前填）。
    ///
    /// 拖动会把整张网格的位置固化下来（这样空槽位也会被保留），锁定时用它记住钉住的位置。
    /// JSON 里仍叫 "PinIndex"（旧字段名），保证老布局能读回来。
    /// </summary>
    [JsonPropertyName("PinIndex")]
    public int? Position { get; set; }
}

/// <summary>`layout.json` 的根对象。</summary>
public sealed class LayoutDocument
{
    public int Version { get; set; } = 1;
    public List<SlotDefinition> Slots { get; set; } = new();
}
