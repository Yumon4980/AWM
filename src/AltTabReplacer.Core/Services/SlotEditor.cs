using System;
using System.Collections.Generic;
using System.Linq;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 槽位的结构编辑：排序、建程序组、建程序组合、移出。
///
/// 全部是**纯函数**（输入槽位列表，返回新的槽位列表），不碰 UI 也不碰文件。
/// 需要可执行文件路径的地方通过 <c>exePath</c> 委托注入（程序组合"未开则启动"要用）。
/// </summary>
public static class SlotEditor
{
    /// <summary>单个程序组合最多包含的程序数。超过不加入。</summary>
    public const int MaxCombinationPrograms = 4;

    /// <summary>移除一级第 index 个槽位（窗口关闭后刷新视图用，不落盘）。</summary>
    public static List<ResolvedSlot> RemoveAt(IReadOnlyList<ResolvedSlot> slots, int index)
    {
        var list = slots.ToList();
        if (index >= 0 && index < list.Count) list.RemoveAt(index);
        return list;
    }

    /// <summary>
    /// 从程序组里移除一个成员（窗口关闭后刷新视图用，不落盘）。
    /// 自动折叠组窗口数掉到 1 时会降级成窗口槽位。
    /// </summary>
    public static List<ResolvedSlot>? RemoveMemberFromGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int memberIndex)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;

        var list = slots.ToList();

        if (group.Children != null)
        {
            var children = group.Children.ToList();
            if (memberIndex < 0 || memberIndex >= children.Count) return null;
            children.RemoveAt(memberIndex);
            list[groupIndex] = BuildGroup(group, children);
            return list;
        }

        // 自动折叠组：按窗口移除
        if (memberIndex < 0 || memberIndex >= group.Windows.Count) return null;
        var windows = group.Windows.ToList();
        var members = group.Members?.ToList();
        windows.RemoveAt(memberIndex);
        members?.RemoveAt(memberIndex);

        if (windows.Count == 0)
        {
            list.RemoveAt(groupIndex);
            return list;
        }

        var procs = windows.Select(w => w.ProcessName)
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        list[groupIndex] = MakeSlot(group.Name, group.CustomName, windows, procs,
            group.MemberOrder?.ToList(), members);
        return list;
    }

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

    // ============================================================
    //  一级：建程序组合 / 建程序组
    // ============================================================

    /// <summary>
    /// 拖动重叠：把 source 并入 target，结果是一个**程序组合**。
    /// 只允许程序 / 程序组合参与；程序组不能进组合（不嵌套）。
    /// 去重后超过 <see cref="MaxCombinationPrograms"/> 个则返回 null（不加入）。
    /// </summary>
    public static List<ResolvedSlot>? CreateCombination(
        IReadOnlyList<ResolvedSlot> slots, int source, int target,
        Func<WindowInfo, string?> exePath)
    {
        if (source < 0 || source >= slots.Count) return null;
        if (target < 0 || target >= slots.Count) return null;
        if (source == target) return null;

        var src = slots[source];
        var dst = slots[target];
        if (src.Kind == SlotKind.Group || dst.Kind == SlotKind.Group) return null;

        var refs = MergeRefs(ProgramRefs(dst, exePath), ProgramRefs(src, exePath));
        if (refs.Count > MaxCombinationPrograms) return null;

        var list = slots.ToList();
        list[target] = new ResolvedSlot
        {
            Kind = SlotKind.Combination,
            Name = string.IsNullOrWhiteSpace(dst.CustomName) ? LayoutResolver.AutoNameForCombination(refs) : dst.CustomName!,
            CustomName = dst.CustomName,
            Windows = dst.Windows.Concat(src.Windows).ToList(),
            Processes = refs.Select(r => r.Process).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Members = refs,
        };
        list.RemoveAt(source);
        return list;
    }

    /// <summary>把 memberIndex 处的程序 / 程序组合加入 groupIndex 处的程序组。</summary>
    public static List<ResolvedSlot>? AddToGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int memberIndex)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        if (memberIndex < 0 || memberIndex >= slots.Count) return null;
        if (groupIndex == memberIndex) return null;

        if (slots[groupIndex].Kind != SlotKind.Group) return null;
        if (slots[memberIndex].Kind == SlotKind.Group) return null;   // 程序组不能嵌套

        var member = slots[memberIndex];
        var list = slots.ToList();
        list.RemoveAt(memberIndex);
        int gi = groupIndex > memberIndex ? groupIndex - 1 : groupIndex;

        var g = list[gi];
        var children = GroupChildren(g);
        children.Add(member);
        list[gi] = BuildGroup(g, children);
        return list;
    }

    /// <summary>新建一个空程序组（界面左上角"+"）。</summary>
    public static List<ResolvedSlot> CreateEmptyGroup(IReadOnlyList<ResolvedSlot> slots, string name = "新程序组")
    {
        var list = slots.ToList();
        list.Add(new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = name,
            CustomName = name,
            Children = new List<ResolvedSlot>(),
            Windows = Array.Empty<WindowInfo>(),
            Processes = Array.Empty<string>(),
            IsEmptyGroup = true,
        });
        return list;
    }

    /// <summary>
    /// 解散程序组合（一级）：把组合里的每个程序摊成独立的程序槽位，占据原位置。
    /// </summary>
    public static List<ResolvedSlot> DissolveCombination(IReadOnlyList<ResolvedSlot> slots, int index)
    {
        var list = slots.ToList();
        if (index < 0 || index >= list.Count) return list;

        var combo = list[index];
        if (combo.Kind != SlotKind.Combination) return list;

        list.RemoveAt(index);
        list.InsertRange(index, ComboPieces(combo));
        return list;
    }

    /// <summary>解散程序组合（二级）：把组合子项摊成组内独立的程序子项。</summary>
    public static List<ResolvedSlot>? DissolveCombinationInGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int childIndex)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;
        if (group.Children == null) return null;
        if (childIndex < 0 || childIndex >= group.Children.Count) return null;

        var combo = group.Children[childIndex];
        if (combo.Kind != SlotKind.Combination) return null;

        var children = group.Children.ToList();
        children.RemoveAt(childIndex);
        children.InsertRange(childIndex, ComboPieces(combo));

        var list = slots.ToList();
        list[groupIndex] = BuildGroup(group, children);
        return list;
    }

    /// <summary>把程序组合展开成若干独立程序槽位（已开的窗口；带成员自定义显示名）。</summary>
    private static List<ResolvedSlot> ComboPieces(ResolvedSlot combo)
    {
        var pieces = new List<ResolvedSlot>(combo.Windows.Count);
        foreach (var w in combo.Windows)
        {
            string? display = null;
            if (combo.Members is { Count: > 0 })
            {
                foreach (var m in combo.Members)
                {
                    if (string.Equals(m.Process, w.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        display = m.DisplayName;
                        break;
                    }
                }
            }
            pieces.Add(MakeWindowSlot(w, display, null));
        }
        return pieces;
    }

    /// <summary>
    /// 删除程序组：只删容器，成员摊回一级。
    /// 手工组摊开子项；自动折叠组摊开每个窗口（相当于旧的"解散分组"）。
    /// </summary>
    public static List<ResolvedSlot> DeleteGroup(IReadOnlyList<ResolvedSlot> slots, int groupIndex)
    {
        var list = slots.ToList();
        if (groupIndex < 0 || groupIndex >= list.Count) return list;

        var group = list[groupIndex];
        if (group.Kind != SlotKind.Group) return list;

        list.RemoveAt(groupIndex);

        if (group.Children != null)
        {
            list.InsertRange(groupIndex, group.Children);
        }
        else
        {
            var pieces = new List<ResolvedSlot>(group.Windows.Count);
            for (int i = 0; i < group.Windows.Count; i++)
            {
                var w = group.Windows[i];
                string? display = group.Members is { Count: > 0 } && i < group.Members.Count
                    ? group.Members[i].DisplayName
                    : null;
                pieces.Add(MakeWindowSlot(w, display, null));
            }
            list.InsertRange(groupIndex, pieces);
        }
        return list;
    }

    // ============================================================
    //  二级：组内排序 / 组内并成组合 / 移出
    // ============================================================

    /// <summary>
    /// 组内成员重排序。手工组重排子项；自动折叠组重排窗口（写 MemberOrder）。
    /// </summary>
    public static List<ResolvedSlot>? ReorderWithinGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int from, int to)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;

        if (group.Children != null)
        {
            var children = group.Children.ToList();
            if (from < 0 || from >= children.Count) return null;
            int at = to;
            var moving = children[from];
            children.RemoveAt(from);
            if (from < at) at--;
            at = Math.Clamp(at, 0, children.Count);
            children.Insert(at, moving);

            var list = slots.ToList();
            list[groupIndex] = BuildGroup(group, children);
            return list;
        }

        // 自动折叠组：重排窗口 + 写 MemberOrder
        var windows = group.Windows.ToList();
        if (from < 0 || from >= windows.Count) return null;
        int insertAt = to;
        var wmoving = windows[from];
        windows.RemoveAt(from);
        if (from < insertAt) insertAt--;
        insertAt = Math.Clamp(insertAt, 0, windows.Count);
        windows.Insert(insertAt, wmoving);

        List<MemberSpec>? members = null;
        if (group.Members is { Count: > 0 } && group.Members.Count == group.Windows.Count)
        {
            var ml = group.Members.ToList();
            var m = ml[from];
            ml.RemoveAt(from);
            ml.Insert(insertAt, m);
            members = ml;
        }

        var outList = slots.ToList();
        outList[groupIndex] = new ResolvedSlot
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
        return outList;
    }

    /// <summary>组内拖动重叠：把两个子项并成一个程序组合子项。超过 4 个返回 null。</summary>
    public static List<ResolvedSlot>? CombineWithinGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int source, int target,
        Func<WindowInfo, string?> exePath)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;
        if (source == target) return null;

        var children = GroupChildren(group);
        if (source < 0 || source >= children.Count) return null;
        if (target < 0 || target >= children.Count) return null;

        var src = children[source];
        var dst = children[target];
        if (src.Kind == SlotKind.Group || dst.Kind == SlotKind.Group) return null;

        var refs = MergeRefs(ProgramRefs(dst, exePath), ProgramRefs(src, exePath));
        if (refs.Count > MaxCombinationPrograms) return null;

        children[target] = new ResolvedSlot
        {
            Kind = SlotKind.Combination,
            Name = string.IsNullOrWhiteSpace(dst.CustomName) ? LayoutResolver.AutoNameForCombination(refs) : dst.CustomName!,
            CustomName = dst.CustomName,
            Windows = dst.Windows.Concat(src.Windows).ToList(),
            Processes = refs.Select(r => r.Process).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Members = refs,
        };
        children.RemoveAt(source);

        var list = slots.ToList();
        list[groupIndex] = BuildGroup(group, children);
        return list;
    }

    /// <summary>二级重命名：改手工程序组里第 childIndex 个子项（程序 / 程序组合）的显示名。</summary>
    public static List<ResolvedSlot>? RenameChild(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int childIndex, string? name)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;
        if (group.Children == null) return null;
        if (childIndex < 0 || childIndex >= group.Children.Count) return null;

        var children = group.Children.ToList();
        children[childIndex] = children[childIndex].WithName(name);

        var list = slots.ToList();
        list[groupIndex] = BuildGroup(group, children);
        return list;
    }

    /// <summary>把组内第 childIndex 个子项移出到组后面（二级拖到面包屑）。手工组限定。</summary>
    public static List<ResolvedSlot>? MoveChildOutOfGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, int childIndex)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;
        if (group.Children == null) return null;                 // 自动组走 MoveWindowOutOfGroup
        if (childIndex < 0 || childIndex >= group.Children.Count) return null;
        if (group.Children.Count <= 1) return null;              // 只剩一个，拆不动

        var children = group.Children.ToList();
        var moved = children[childIndex];
        children.RemoveAt(childIndex);

        var list = slots.ToList();
        list[groupIndex] = BuildGroup(group, children);
        list.Insert(groupIndex + 1, moved);
        return list;
    }

    /// <summary>
    /// 把**单个窗口**从自动折叠组里拆出来（按窗口，不按进程）。
    /// 手工组请用 <see cref="MoveChildOutOfGroup"/>。
    /// </summary>
    public static List<ResolvedSlot>? MoveWindowOutOfGroup(
        IReadOnlyList<ResolvedSlot> slots, int groupIndex, WindowInfo window)
    {
        if (groupIndex < 0 || groupIndex >= slots.Count) return null;
        var group = slots[groupIndex];
        if (group.Kind != SlotKind.Group) return null;
        if (group.Windows.Count <= 1) return null;

        int idx = -1;
        for (int i = 0; i < group.Windows.Count; i++)
        {
            if (group.Windows[i].Hwnd == window.Hwnd) { idx = i; break; }
        }
        if (idx < 0) return null;

        var srcMembers = group.Members is { Count: > 0 } && group.Members.Count == group.Windows.Count
            ? group.Members
            : null;

        var restWindows = new List<WindowInfo>(group.Windows.Count - 1);
        for (int i = 0; i < group.Windows.Count; i++)
            if (i != idx) restWindows.Add(group.Windows[i]);

        List<MemberSpec>? restMembers = null;
        if (srcMembers != null)
        {
            restMembers = new List<MemberSpec>(srcMembers.Count - 1);
            for (int i = 0; i < srcMembers.Count; i++)
                if (i != idx) restMembers.Add(srcMembers[i]);
        }

        var restProcs = restWindows.Select(w => w.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var restOrder = group.MemberOrder?
            .Where(t => !string.Equals(t, window.Title, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string? movedDisplay = srcMembers?[idx].DisplayName;

        var list = slots.ToList();
        list[groupIndex] = MakeSlot(group.Name, group.CustomName, restWindows, restProcs, restOrder, restMembers);
        list.Insert(groupIndex + 1, MakeWindowSlot(window, movedDisplay, null));
        return list;
    }

    // ============================================================
    //  内部辅助
    // ============================================================

    /// <summary>取程序组的子项；自动折叠组现场物化成"每窗口一个程序子项"。</summary>
    private static List<ResolvedSlot> GroupChildren(ResolvedSlot group)
    {
        if (group.Children != null) return group.Children.ToList();

        var children = new List<ResolvedSlot>(group.Windows.Count);
        for (int i = 0; i < group.Windows.Count; i++)
        {
            var w = group.Windows[i];
            string? display = group.Members is { Count: > 0 } && i < group.Members.Count
                ? group.Members[i].DisplayName
                : null;
            children.Add(MakeWindowSlot(w, display, null));
        }
        return children;
    }

    /// <summary>用新的子项列表重建程序组（展平窗口 / 进程）。</summary>
    private static ResolvedSlot BuildGroup(ResolvedSlot g, List<ResolvedSlot> children)
    {
        return new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = g.Name,
            CustomName = g.CustomName,
            Children = children,
            Windows = children.SelectMany(c => c.Windows).ToList(),
            Processes = children.SelectMany(c => c.Processes)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            IsEmptyGroup = children.Count == 0,
        };
    }

    /// <summary>把一个槽位展开成"程序引用"列表（程序组合的成员）。</summary>
    private static List<MemberSpec> ProgramRefs(ResolvedSlot slot, Func<WindowInfo, string?> exePath)
    {
        if (slot.Kind == SlotKind.Combination && slot.Members is { Count: > 0 })
            return slot.Members.ToList();

        var result = new List<MemberSpec>(slot.Windows.Count);
        for (int i = 0; i < slot.Windows.Count; i++)
        {
            var w = slot.Windows[i];
            string? display = slot.Members is { Count: > 0 } && i < slot.Members.Count
                ? slot.Members[i].DisplayName : null;
            string? exe = slot.Members is { Count: > 0 } && i < slot.Members.Count
                ? slot.Members[i].ExePath : null;
            result.Add(new MemberSpec(w.ProcessName, w.Title)
            {
                DisplayName = display,
                ExePath = exe ?? exePath(w),
            });
        }
        return result;
    }

    /// <summary>合并两组程序引用，按进程去重（同一个程序只留一个）。</summary>
    private static List<MemberSpec> MergeRefs(IEnumerable<MemberSpec> a, IEnumerable<MemberSpec> b)
    {
        var result = new List<MemberSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in a.Concat(b))
        {
            if (string.IsNullOrEmpty(s.Process)) continue;
            if (seen.Add(s.Process)) result.Add(s);
        }
        return result;
    }

    private static ResolvedSlot MakeWindowSlot(WindowInfo w, string? display, string? customName)
    {
        string? effectiveCustom = !string.IsNullOrWhiteSpace(display) ? display : customName;
        return new ResolvedSlot
        {
            Kind = SlotKind.Window,
            Name = !string.IsNullOrWhiteSpace(display) ? display!
                : !string.IsNullOrWhiteSpace(customName) ? customName!
                : w.Title,
            CustomName = effectiveCustom,
            Windows = new[] { w },
            Processes = new[] { w.ProcessName },
            Members = new[] { new MemberSpec(w.ProcessName, w.Title) { DisplayName = display } },
        };
    }

    /// <summary>剩 1 个窗口就降级成窗口槽位，保持"单成员自动组自动解散"的不变量。</summary>
    private static ResolvedSlot MakeSlot(string? name, string? customName,
        List<WindowInfo> windows, List<string> procs,
        IReadOnlyList<string>? memberOrder = null,
        IReadOnlyList<MemberSpec>? members = null)
    {
        bool isGroup = windows.Count > 1;

        if (!isGroup)
        {
            string? display = members is { Count: > 0 } ? members[0].DisplayName : null;
            return MakeWindowSlot(windows[0], display, customName);
        }

        return new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = string.IsNullOrWhiteSpace(name) ? LayoutResolver.AutoName(windows) : name!,
            CustomName = customName,
            Windows = windows,
            Processes = procs,
            MemberOrder = members is { Count: > 0 } ? null : memberOrder,
            Members = members,
        };
    }
}
