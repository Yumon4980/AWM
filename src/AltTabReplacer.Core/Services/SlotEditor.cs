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
            // 关键：记下**具体是哪些窗口**，而不是"这些进程的全部窗口"。
            // 否则把一个 VS Code 窗口拖进分组会把其余 VS Code 窗口一起拽进来。
            // 同时带上各方已有的自定义显示名，别在合并时丢掉。
            Members = MergeMemberSpecs(dst, src),
        };
        list.RemoveAt(source);
        return list;
    }

    /// <summary>合并两个槽位的成员描述，保留各自已有的自定义显示名。顺序与 Windows 拼接顺序一致。</summary>
    private static List<MemberSpec> MergeMemberSpecs(ResolvedSlot dst, ResolvedSlot src)
    {
        var specs = new List<MemberSpec>(dst.Windows.Count + src.Windows.Count);
        foreach (var s in new[] { dst, src })
        {
            for (int i = 0; i < s.Windows.Count; i++)
            {
                var w = s.Windows[i];
                string? display = s.Members is { Count: > 0 } && i < s.Members.Count
                    ? s.Members[i].DisplayName
                    : (s.Kind == SlotKind.Window ? s.CustomName : null);
                specs.Add(new MemberSpec(w.ProcessName, w.Title) { DisplayName = display });
            }
        }
        return specs;
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
        // 原组保留用户定过的顺序，但要剔除被移走的那个
        var restOrder = group.MemberOrder?
            .Where(t => !movedWindows.Any(m => string.Equals(m.Title, t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        list[groupIndex] = MakeSlot(group.Name, group.CustomName, restWindows, restProcs, restOrder);
        list.Insert(groupIndex + 1, MakeSlot(null, null, movedWindows, new List<string> { processName }));
        return list;
    }

    /// <summary>
    /// 把**单个窗口**从组里拆出来，单独成为一个新槽位。
    ///
    /// 与 <see cref="MoveOutOfGroup"/> 的区别：后者按进程拆，对单进程组（自动折叠出来的）失效；
    /// 本方法按窗口拆，单进程组里多窗口时也能逐一移出。
    /// 组内只剩这个窗口被移走时返回 null —— 没有可拆的了。
    /// </summary>
    public static List<ResolvedSlot>? MoveWindowOutOfGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, WindowInfo window)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;
        if (group.Windows.Count <= 1) return null;       // 唯一成员，拆不动

        // 找到这个窗口在组里的索引
        int idx = -1;
        for (int i = 0; i < group.Windows.Count; i++)
        {
            if (group.Windows[i].Hwnd == window.Hwnd) { idx = i; break; }
        }
        if (idx < 0) return null;

        // 与 Windows 一一对应的成员描述（只有手工组才有）
        var srcMembers = group.Members is { Count: > 0 } && group.Members.Count == group.Windows.Count
            ? group.Members
            : null;

        var restWindows = new List<WindowInfo>(group.Windows.Count - 1);
        for (int i = 0; i < group.Windows.Count; i++)
            if (i != idx) restWindows.Add(group.Windows[i]);

        // 剩余组的成员描述（剔除被移走的那个），保住各自的自定义显示名，
        // 也避免剩余组退化成"按进程认领"从而又去抢别的组的窗口。
        List<MemberSpec>? restMembers = null;
        if (srcMembers != null)
        {
            restMembers = new List<MemberSpec>(srcMembers.Count - 1);
            for (int i = 0; i < srcMembers.Count; i++)
                if (i != idx) restMembers.Add(srcMembers[i]);
        }

        // 剩下的窗口还覆盖哪些进程
        var restProcs = restWindows
            .Select(w => w.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 剩下的组仍然按用户的拖动顺序排，但剔除被移走窗口的标题
        var restOrder = group.MemberOrder?
            .Where(t => !string.Equals(t, window.Title, StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? movedDisplay = srcMembers?[idx].DisplayName;

        var list = slots.ToList();
        list[groupIndex] = MakeSlot(group.Name, group.CustomName, restWindows, restProcs, restOrder, restMembers);
        // 被移出的窗口单独成槽，位置在原组之后；保留它自己的自定义显示名
        list.Insert(groupIndex + 1, new ResolvedSlot
        {
            Kind = SlotKind.Window,
            Name = string.IsNullOrWhiteSpace(movedDisplay) ? window.Title : movedDisplay!,
            CustomName = movedDisplay,
            Windows = new[] { window },
            Processes = new[] { window.ProcessName },
            Members = new[] { new MemberSpec(window.ProcessName, window.Title) { DisplayName = movedDisplay } },
        });
        return list;
    }

    /// <summary>
    /// 组内成员重排序。**只允许排序，不允许再嵌套分组**（组的层级只有两级）。
    ///
    /// 重排后会写入 <see cref="ResolvedSlot.MemberOrder"/>（窗口标题列表）作为持久化提示。
    /// 不写的话下次解析又会回到 z-order，用户的拖动就白做了。
    /// </summary>
    public static List<ResolvedSlot>? ReorderWithinGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int from, int to)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;

        var windows = group.Windows.ToList();
        if (from < 0 || from >= windows.Count) return null;

        int insertAt = to;
        var moving = windows[from];
        windows.RemoveAt(from);
        if (from < insertAt) insertAt--;          // 移除自身后目标索引左移
        insertAt = Math.Clamp(insertAt, 0, windows.Count);
        windows.Insert(insertAt, moving);

        // 手工组（有 Members）要同步重排 Members，否则成员的自定义显示名会错位。
        // 自动折叠组没有 Members，退回写 MemberOrder（窗口标题）作为顺序提示。
        List<MemberSpec>? members = null;
        if (group.Members is { Count: > 0 } && group.Members.Count == group.Windows.Count)
        {
            var ml = group.Members.ToList();
            var m = ml[from];
            ml.RemoveAt(from);
            ml.Insert(insertAt, m);
            members = ml;
        }

        var list = slots.ToList();
        list[groupIndex] = new ResolvedSlot
        {
            Kind = group.Kind,
            Name = group.Name,
            CustomName = group.CustomName,
            Windows = windows,
            Processes = group.Processes,
            IsOverflow = group.IsOverflow,
            Members = members,
            MemberOrder = members == null ? windows.Select(w => w.Title).ToList() : null,
        };
        return list;
    }

    /// <summary>
    /// 解散分组：拆成"每个窗口一个槽位"。
    ///
    /// 每个槽位记的是**具体那个窗口**（进程 + 标题），不是整个进程，
    /// 所以不会在下次解析时又被折叠回去——这正是"解散分组点了没反应"的原因。
    /// 跨进程组和单进程组用同一套机制，不再需要区分。
    /// </summary>
    public static List<ResolvedSlot> DissolveGroup(IReadOnlyList<ResolvedSlot> slots, int groupIndex)
    {
        var list = slots.ToList();
        if (groupIndex < 0 || groupIndex >= list.Count) return list;

        var group = list[groupIndex];
        if (group.Kind != SlotKind.Group) return list;

        list.RemoveAt(groupIndex);

        var pieces = new List<ResolvedSlot>(group.Windows.Count);
        for (int i = 0; i < group.Windows.Count; i++)
        {
            var w = group.Windows[i];
            // 保住成员在二级右键改过的显示名
            string? display = group.Members is { Count: > 0 } && i < group.Members.Count
                ? group.Members[i].DisplayName
                : null;
            pieces.Add(new ResolvedSlot
            {
                Kind = SlotKind.Window,
                Name = string.IsNullOrWhiteSpace(display) ? w.Title : display!,
                CustomName = display,
                Windows = new[] { w },
                Processes = new[] { w.ProcessName },
                Members = new[] { new MemberSpec(w.ProcessName, w.Title) { DisplayName = display } },
            });
        }

        list.InsertRange(groupIndex, pieces);
        return list;
    }

    /// <summary>剩 1 个窗口就降级成窗口槽位，保持"单成员组自动解散"的不变量。</summary>
    private static ResolvedSlot MakeSlot(string? name, string? customName,
        List<WindowInfo> windows, List<string> procs,
        IReadOnlyList<string>? memberOrder = null,
        IReadOnlyList<MemberSpec>? members = null)
    {
        bool isGroup = windows.Count > 1;

        if (!isGroup)
        {
            // 单窗口：成员自定义显示名 > 槽位自定义名 > 窗口标题
            string? display = members is { Count: > 0 } ? members[0].DisplayName : null;
            string finalName = !string.IsNullOrWhiteSpace(display) ? display!
                : !string.IsNullOrWhiteSpace(customName) ? customName!
                : windows[0].Title;
            return new ResolvedSlot
            {
                Kind = SlotKind.Window,
                Name = finalName,
                CustomName = !string.IsNullOrWhiteSpace(display) ? display : customName,
                Windows = windows,
                Processes = procs,
                Members = new[] { new MemberSpec(windows[0].ProcessName, windows[0].Title) { DisplayName = display } },
            };
        }

        return new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = string.IsNullOrWhiteSpace(name) ? LayoutResolver.AutoName(windows) : name!,
            CustomName = customName,
            Windows = windows,
            Processes = procs,
            // 有成员描述时顺序由 Members 决定，不再写 MemberOrder
            MemberOrder = members is { Count: > 0 } ? null : memberOrder,
            Members = members,
        };
    }
}
