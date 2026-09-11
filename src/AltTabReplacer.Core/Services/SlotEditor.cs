using System;
using System.Collections.Generic;
using System.Linq;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 槽位的结构编辑：排序、并组、移出组。
///
/// 全部是**纯函数**（输入槽位列表，返回新的槽位列表），不碰 UI 也不碰文件，
/// 这样第 6 节那张"七种拖放组合"的语义表才能被测试覆盖。
/// </summary>
public static class SlotEditor
{
    /// <summary>把 from 位置的槽位移到 to 位置（纯排序）。</summary>
    public static List<ResolvedSlot> Reorder(IReadOnlyList<ResolvedSlot> slots, int from, int to)
    {
        var list = slots.ToList();
        if (from < 0 || from >= list.Count) return list;

        var moving = list[from];
        list.RemoveAt(from);
        if (from < to) to--;                     // 移除自身后，目标索引左移
        to = Math.Clamp(to, 0, list.Count);
        list.Insert(to, moving);
        return list;
    }

    /// <summary>
    /// 把 source 槽位并入 target 槽位，结果一定是个组。
    /// 覆盖"窗口→窗口（新建组）""窗口→组（加入）""组→组（合并）"三种情况。
    /// </summary>
    public static List<ResolvedSlot> Merge(IReadOnlyList<ResolvedSlot> slots, int source, int target)
    {
        var list = slots.ToList();
        if (source < 0 || source >= list.Count) return list;
        if (target < 0 || target >= list.Count) return list;
        if (source == target) return list;

        var src = list[source];
        var dst = list[target];

        var mergedProcs = dst.Processes.Concat(src.Processes)
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mergedWindows = dst.Windows.Concat(src.Windows).ToList();

        // 目标的名字优先保留：本来是组就沿用组名，用户改过名就沿用改过的名
        string? keep = dst.CustomName ?? (dst.Kind == SlotKind.Group ? dst.Name : null);

        list[target] = new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = keep ?? LayoutResolver.AutoName(mergedWindows),
            CustomName = dst.CustomName,
            Windows = mergedWindows,
            Processes = mergedProcs,
        };
        list.RemoveAt(source);
        return list;
    }

    /// <summary>
    /// 把某个进程从组里拆出来，成为紧随其后的独立槽位。
    ///
    /// 组只剩一个进程时返回 null —— 身份锚定在进程上，
    /// 自动折叠出来的单进程组没法再拆出"其中一个窗口"。调用方据此给提示。
    /// </summary>
    public static List<ResolvedSlot>? MoveOutOfGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, string processName)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;

        var restProcs = group.Processes
            .Where(p => !string.Equals(p, processName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (restProcs.Count == 0) return null;          // 单进程组，拆不动

        bool IsMoved(WindowInfo w) =>
            string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase);

        var movedWindows = group.Windows.Where(IsMoved).ToList();
        var restWindows = group.Windows.Where(w => !IsMoved(w)).ToList();
        if (movedWindows.Count == 0 || restWindows.Count == 0) return null;

        var list = slots.ToList();
        list[groupIndex] = MakeSlot(group.Name, group.CustomName, restWindows, restProcs);
        list.Insert(groupIndex + 1, MakeSlot(null, null, movedWindows, new List<string> { processName }));
        return list;
    }

    /// <summary>剩 1 个窗口就降级成窗口槽位，保持"单成员组自动解散"的不变量。</summary>
    private static ResolvedSlot MakeSlot(string? name, string? customName,
        List<WindowInfo> windows, List<string> procs)
    {
        bool isGroup = windows.Count > 1;
        return new ResolvedSlot
        {
            Kind = isGroup ? SlotKind.Group : SlotKind.Window,
            Name = isGroup
                ? (string.IsNullOrWhiteSpace(name) ? LayoutResolver.AutoName(windows) : name!)
                : (string.IsNullOrWhiteSpace(customName) ? windows[0].Title : customName!),
            CustomName = customName,
            Windows = windows,
            Processes = procs,
        };
    }
}
