using System;
using System.Collections.Generic;
using System.Linq;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

/// <summary>运行时解析出来的一个槽位。</summary>
public sealed class ResolvedSlot
{
    public SlotKind Kind { get; init; }

    /// <summary>显示名。窗口槽位默认是窗口标题，组槽位默认是自动生成的名字。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 用户右键改过的名字。null 表示没改过，显示名走默认逻辑。
    /// 持久化时写回 layout.json，是唯一需要保存的名字来源。
    /// </summary>
    public string? CustomName { get; init; }

    /// <summary>本槽位下的窗口。Kind=Window 时恰好 1 个。</summary>
    public IReadOnlyList<WindowInfo> Windows { get; init; } = Array.Empty<WindowInfo>();

    /// <summary>组对应的进程集合，写回布局时用。Kind=Window 时是该窗口的进程。</summary>
    public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();

    /// <summary>溢出组（"更多…"）不写回布局，只是运行时的容器。</summary>
    public bool IsOverflow { get; init; }

    /// <summary>
    /// 组内成员的显示顺序（窗口标题）。null 表示按 z-order。
    /// 只有用户在组内手动拖过才会有值。
    /// </summary>
    public IReadOnlyList<string>? MemberOrder { get; init; }

    /// <summary>
    /// 精确到窗口的成员描述。null 表示"按进程取全部窗口"（自动折叠语义）。
    /// 手工建的分组 / 解散后的槽位会有值，顺序即 <see cref="Windows"/> 的顺序。
    /// </summary>
    public IReadOnlyList<MemberSpec>? Members { get; init; }

    /// <summary>
    /// 旧字段：解散单进程组时置位。现已由 <see cref="Members"/> 取代，仅为兼容旧布局保留。
    /// </summary>
    public bool NoAutoGroup { get; init; }

    public WindowInfo? SingleWindow => Windows.Count > 0 ? Windows[0] : null;
    public int Count => Windows.Count;

    /// <summary>返回一个改了名字的副本。name 传 null 表示恢复默认名。</summary>
    public ResolvedSlot WithName(string? name) => new()
    {
        Kind = Kind,
        Name = string.IsNullOrWhiteSpace(name)
            ? (Kind == SlotKind.Group ? LayoutResolver.AutoName(Windows) : (SingleWindow?.Title ?? ""))
            : name!,
        CustomName = string.IsNullOrWhiteSpace(name) ? null : name,
        Windows = Windows,
        Processes = Processes,
        IsOverflow = IsOverflow,
        MemberOrder = MemberOrder,
        Members = Members,          // 必须带上：丢了它就退化成"该进程的全部窗口"
        NoAutoGroup = NoAutoGroup,
    };
}

/// <summary>
/// 把「枚举到的窗口」+「持久化的布局」解析成实际的 16 槽位树。
///
/// 规则（依次施加）：
///   1) 按 <see cref="LayoutDocument.Slots"/> 的顺序安置已知进程的窗口
///   2) 布局里没提到的进程按 z-order 追加在后面
///   3) 同一进程窗口数 ≥ <c>autoGroupThreshold</c> 时自动折叠成一个组，占一个槽位
///   4) 组内成员按 z-order 排（最近激活的在前），**不做持久化**
///   5) 只剩 1 个成员的组自动降级为窗口槽位，不让用户白按一次
///   6) 槽位超过 16 个时，第 16 格变成"更多…"溢出组，装下所有余量
///
/// 纯函数，不碰文件也不碰 UI，方便单测。
/// </summary>
public static class LayoutResolver
{
    public static IReadOnlyList<ResolvedSlot> Resolve(
        IReadOnlyList<WindowInfo> windows,
        LayoutDocument layout,
        int autoGroupThreshold = 2,
        int pageSize = KeyMap.Size)
    {
        if (windows.Count == 0) return Array.Empty<ResolvedSlot>();

        // windows 已按 z-order 排好（最近激活的在前），后面所有"组内顺序"都直接沿用这个次序
        var remaining = new List<WindowInfo>(windows);
        var slots = new List<ResolvedSlot>();

        // ---- 0) 跨槽位先做一遍"精确匹配"，把所有 (进程, 标题) 预留掉 ----
        // 必须整体先做。否则某个槽位的"按进程兜底"（TakeAnyOfProcess）会抢走
        // 后面槽位本该精确匹配的窗口——用户看到的是"关掉一个窗口后两个分组被合并了"。
        var exactBySlot = new Dictionary<int, WindowInfo?[]>();
        for (int d = 0; d < layout.Slots.Count; d++)
        {
            var def = layout.Slots[d];
            if (def.Members is not { Count: > 0 }) continue;
            var arr = new WindowInfo?[def.Members.Count];
            for (int i = 0; i < def.Members.Count; i++)
                arr[i] = TakeExact(remaining, def.Members[i]);
            exactBySlot[d] = arr;
        }

        // ---- 1) 再按布局顺序组装。精确匹配没中的，才允许按进程兜底 ----
        for (int d = 0; d < layout.Slots.Count; d++)
        {
            var def = layout.Slots[d];
            var taken = new List<WindowInfo>();
            // 与 taken 一一对应的 spec 列表：某个成员这轮没认领到窗口时，
            // 必须把它从 Members 里一起剔除，否则 Windows[i] 会和 Members[i] 错位，
            // 成员的自定义显示名就会张冠李戴。
            List<MemberSpec>? matchedSpecs = null;

            if (def.Members is { Count: > 0 })
            {
                // 精确到窗口：逐个 spec 认领，顺序就是 Members 的顺序
                var arr = exactBySlot[d];
                matchedSpecs = new List<MemberSpec>(def.Members.Count);
                for (int i = 0; i < def.Members.Count; i++)
                {
                    var w = arr[i] ?? TakeAnyOfProcess(remaining, def.Members[i].Process);
                    if (w == null) continue;          // 这个成员没开，跳过它和它的 spec
                    taken.Add(w);
                    matchedSpecs.Add(def.Members[i]);
                }
            }
            else
            {
                if (def.Processes.Count == 0) continue;
                foreach (var proc in def.Processes)
                {
                    for (int i = remaining.Count - 1; i >= 0; i--)
                    {
                        if (string.Equals(remaining[i].ProcessName, proc, StringComparison.OrdinalIgnoreCase))
                        {
                            taken.Add(remaining[i]);
                            remaining.RemoveAt(i);
                        }
                    }
                }
                // 恢复 z-order（上面倒序遍历是为了安全删除）
                taken = SortByZOrder(taken, windows);
            }

            if (taken.Count == 0) continue;   // 这条布局对应的窗口这次一个都没开，丢弃

            // 旧字段兼容：解散单进程组 → 每窗口一个槽位
            if (def.NoAutoGroup && def.Members is null or { Count: 0 } && taken.Count > 1)
            {
                foreach (var w in ApplyMemberOrder(taken, def.MemberOrder))
                {
                    slots.Add(new ResolvedSlot
                    {
                        Kind = SlotKind.Window,
                        Name = w.Title,
                        Windows = new[] { w },
                        Processes = new[] { w.ProcessName },
                        Members = new[] { new MemberSpec(w.ProcessName, w.Title) },
                    });
                }
                continue;
            }

            var procs = matchedSpecs is { Count: > 0 }
                ? matchedSpecs.Select(m => m.Process).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : def.Processes;

            slots.AddRange(MakeSlots(def.Kind, def.Name, procs, taken, autoGroupThreshold,
                def.MemberOrder, matchedSpecs));
        }

        // ---- 2) 布局里没提到的进程，按 z-order 追加 ----
        foreach (var group in remaining.GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var members = SortByZOrder(group.ToList(), windows);
            slots.AddRange(MakeSlots(SlotKind.Window, null, new[] { group.Key }, members, autoGroupThreshold));
        }

        // ---- 3) 溢出处理 ----
        return ApplyOverflow(slots, pageSize);
    }

    /// <summary>
    /// 按用户拖出来的顺序排组成员。
    /// 标题对不上的（换过文件、换过标签）退回 z-order 排在已知成员之后——
    /// 宁可位置不理想，也不能因为标题变了就把窗口藏起来。
    /// </summary>
    private static List<WindowInfo> ApplyMemberOrder(List<WindowInfo> members, IReadOnlyList<string>? order)
    {
        if (order == null || order.Count == 0) return members;

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < order.Count; i++)
        {
            if (!rank.ContainsKey(order[i])) rank[order[i]] = i;
        }

        return members
            .Select((w, i) => (w, i))
            .OrderBy(x => rank.TryGetValue(x.w.Title, out int r) ? r : int.MaxValue)
            .ThenBy(x => x.i)                       // 未匹配的保持原 z-order
            .Select(x => x.w)
            .ToList();
    }

    /// <summary>按 (进程, 标题) 精确认领一个窗口，并从待分配里移除。</summary>
    private static WindowInfo? TakeExact(List<WindowInfo> remaining, MemberSpec spec)
    {
        if (string.IsNullOrEmpty(spec.Title)) return null;
        for (int i = 0; i < remaining.Count; i++)
        {
            if (string.Equals(remaining[i].ProcessName, spec.Process, StringComparison.OrdinalIgnoreCase)
                && string.Equals(remaining[i].Title, spec.Title, StringComparison.OrdinalIgnoreCase))
            {
                var w = remaining[i];
                remaining.RemoveAt(i);
                return w;
            }
        }
        return null;
    }

    /// <summary>
    /// 标题对不上时的兜底：认领该进程的任意一个窗口。
    /// 标题会变（VS Code 换文件），没有兜底的话那个窗口就会掉出分组、
    /// 用户看到的是"分组里的窗口莫名其妙少了一个"。
    /// </summary>
    private static WindowInfo? TakeAnyOfProcess(List<WindowInfo> remaining, string process)
    {
        for (int i = 0; i < remaining.Count; i++)
        {
            if (string.Equals(remaining[i].ProcessName, process, StringComparison.OrdinalIgnoreCase))
            {
                var w = remaining[i];
                remaining.RemoveAt(i);
                return w;
            }
        }
        return null;
    }

    /// <summary>按窗口在原始 z-order 列表中的位置排序。</summary>
    private static List<WindowInfo> SortByZOrder(List<WindowInfo> subset, IReadOnlyList<WindowInfo> zorder)
    {
        var rank = new Dictionary<IntPtr, int>();
        for (int i = 0; i < zorder.Count; i++) rank[zorder[i].Hwnd] = i;
        return subset.OrderBy(w => rank.TryGetValue(w.Hwnd, out int r) ? r : int.MaxValue).ToList();
    }

    /// <summary>
    /// 决定一批窗口该做成一个组槽位，还是拆成多个独立的窗口槽位。
    /// 不成组时必须**一窗一槽**——塞进同一个槽位会让它们互相看不见。
    /// 1 个窗口永远是窗口槽位，这就是"只剩 1 个成员的组自动解散"。
    /// </summary>
    private static IEnumerable<ResolvedSlot> MakeSlots(
        SlotKind declaredKind, string? name, IReadOnlyList<string> processes,
        List<WindowInfo> members, int autoGroupThreshold,
        IReadOnlyList<string>? memberOrder = null,
        IReadOnlyList<MemberSpec>? memberSpecs = null)
    {
        bool isGroup = members.Count > 1
            && (declaredKind == SlotKind.Group || members.Count >= autoGroupThreshold);

        if (isGroup)
        {
            var ordered = memberSpecs is { Count: > 0 }
                ? members                                    // 手工组：顺序由 Members 决定
                : ApplyMemberOrder(members, memberOrder);    // 自动折叠组：按用户拖过的顺序
            yield return new ResolvedSlot
            {
                Kind = SlotKind.Group,
                Name = string.IsNullOrWhiteSpace(name) ? AutoName(ordered) : name!,
                CustomName = string.IsNullOrWhiteSpace(name) ? null : name,
                Windows = ordered,
                Processes = processes.ToArray(),
                MemberOrder = memberSpecs is { Count: > 0 } ? null : memberOrder,
                Members = memberSpecs,
            };
            yield break;
        }

        for (int i = 0; i < members.Count; i++)
        {
            var w = members[i];
            // 用户改过名就用用户的，否则用窗口标题
            bool named = !string.IsNullOrWhiteSpace(name);
            // 成员的自定义显示名（二级右键"重命名程序"）优先；组解散成单窗口时也保住
            string? memberName = memberSpecs is { Count: > 0 } && i < memberSpecs.Count
                ? memberSpecs[i].DisplayName
                : null;
            yield return new ResolvedSlot
            {
                Kind = SlotKind.Window,
                Name = named ? name! : (string.IsNullOrWhiteSpace(memberName) ? w.Title : memberName!),
                CustomName = named ? name : memberName,
                Windows = new[] { w },
                Processes = new[] { w.ProcessName },
                Members = new[] { new MemberSpec(w.ProcessName, w.Title) { DisplayName = memberName } },
            };
        }
    }

    /// <summary>自动组名：单进程用进程名去掉 .exe，多进程用"首个进程 +N"。</summary>
    public static string AutoName(IReadOnlyList<WindowInfo> members)
    {
        if (members.Count == 0) return "空组";
        var distinct = members.Select(m => m.ProcessName)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
        var head = StripExe(distinct[0]);
        return distinct.Count == 1 ? head : $"{head} +{distinct.Count - 1}";
    }

    private static string StripExe(string proc) =>
        proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? proc[..^4]
            : proc;

    /// <summary>槽位多于一页时，最后一格改成"更多…"组装下余量。</summary>
    private static IReadOnlyList<ResolvedSlot> ApplyOverflow(List<ResolvedSlot> slots, int pageSize)
    {
        if (pageSize <= 0 || slots.Count <= pageSize) return slots;

        var head = slots.Take(pageSize - 1).ToList();
        var tail = slots.Skip(pageSize - 1).ToList();

        var overflowWindows = tail.SelectMany(s => s.Windows).ToList();
        var overflowProcs = tail.SelectMany(s => s.Processes)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
        // 把各槽位已有的显示名（成员自定义名 / 窗口自定义名）带进溢出组，
        // 否则进入"更多…"后，二级右键"重命名程序"改的名字会消失。
        var overflowMembers = tail.SelectMany(SlotMemberSpecs).ToList();

        head.Add(new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = $"更多… ({overflowWindows.Count})",
            Windows = overflowWindows,
            Processes = overflowProcs,
            Members = overflowMembers,
            IsOverflow = true,
        });
        return head;
    }

    /// <summary>把一个槽位的窗口展开成成员描述，带上各自的显示名（若有）。</summary>
    private static IEnumerable<MemberSpec> SlotMemberSpecs(ResolvedSlot s)
    {
        for (int i = 0; i < s.Windows.Count; i++)
        {
            var w = s.Windows[i];
            string? display = null;
            if (s.Members is { Count: > 0 } && i < s.Members.Count)
                display = s.Members[i].DisplayName;
            else if (s.Kind == SlotKind.Window)
                display = s.CustomName;
            yield return new MemberSpec(w.ProcessName, w.Title) { DisplayName = display };
        }
    }

    /// <summary>把当前槽位顺序写成可持久化的布局（溢出组会被摊平）。</summary>
    public static LayoutDocument ToDocument(IReadOnlyList<ResolvedSlot> slots)
    {
        var doc = new LayoutDocument();

        foreach (var s in slots)
        {
            if (s.IsOverflow)
            {
                // 溢出组只是运行时容器，摊平成各自独立的槽位，别把它固化下来。
                // 成员若被二级右键改过显示名，摊平后要落到这个窗口槽位的 Name 上，别丢。
                for (int i = 0; i < s.Windows.Count; i++)
                {
                    var w = s.Windows[i];
                    string? display = s.Members is { Count: > 0 } && i < s.Members.Count
                        ? s.Members[i].DisplayName
                        : null;
                    doc.Slots.Add(new SlotDefinition
                    {
                        Kind = SlotKind.Window,
                        Name = display,
                        Processes = new List<string> { w.ProcessName },
                        Members = new List<MemberSpec> { new(w.ProcessName, w.Title) { DisplayName = display } },
                    });
                }
                continue;
            }

            // 精确到窗口的槽位（手工组 / 解散后的窗口 / 单个窗口）：
            // 每个槽位写一条定义，各自认领具体窗口。
            // 这样每个槽位的名字、顺序都能独立保留——旧实现把同进程的槽位合并成一条，
            // 结果**重命名的名字直接丢了**，而且一条定义会去抢该进程的全部窗口。
            if (s.Members is { Count: > 0 })
            {
                doc.Slots.Add(new SlotDefinition
                {
                    Kind = s.Kind,
                    Name = s.CustomName,
                    Processes = s.Processes.ToList(),
                    Members = s.Members.ToList(),
                });
                continue;
            }

            doc.Slots.Add(new SlotDefinition
            {
                Kind = s.Kind,
                // 只有用户改过的名字才落盘；没改过的下次解析时重新推导（窗口标题会变，存了反而会过期）
                Name = s.CustomName,
                Processes = s.Processes.ToList(),
                // 只有用户在组内拖过才记顺序
                MemberOrder = s.MemberOrder?.ToList(),
            });
        }
        return doc;
    }
}
