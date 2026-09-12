using System;
using System.Collections.Generic;
using System.Linq;
using AltTabReplacer.Core.Models;

namespace AltTabReplacer.Core.Services;

/// <summary>运行时解析出来的一个槽位。</summary>
public sealed class ResolvedSlot
{
    public SlotKind Kind { get; init; }

    /// <summary>显示名。程序默认是窗口标题，程序组/程序组合默认是自动生成的名字。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 用户右键改过的名字。null 表示没改过，显示名走默认逻辑。
    /// 持久化时写回 layout.json，是唯一需要保存的名字来源。
    /// </summary>
    public string? CustomName { get; init; }

    /// <summary>本槽位下的窗口（展平）。程序 = 1 个；程序组/程序组合 = 全部成员窗口。</summary>
    public IReadOnlyList<WindowInfo> Windows { get; init; } = Array.Empty<WindowInfo>();

    /// <summary>
    /// 程序组的子项（程序 / 程序组合）。null = 不是手工程序组：
    /// 程序 / 程序组合 / 自动折叠组都用 <see cref="Windows"/> 平铺表示。
    /// </summary>
    public IReadOnlyList<ResolvedSlot>? Children { get; init; }

    /// <summary>槽位涉及的进程集合，写回布局时用。</summary>
    public IReadOnlyList<string> Processes { get; init; } = Array.Empty<string>();

    /// <summary>溢出组（"更多…"）不写回布局，只是运行时的容器。</summary>
    public bool IsOverflow { get; init; }

    /// <summary>
    /// 自动折叠组的成员显示顺序（窗口标题）。null 表示按 z-order。
    /// 只有用户在组内手动拖过才会有值。
    /// </summary>
    public IReadOnlyList<string>? MemberOrder { get; init; }

    /// <summary>
    /// 程序 / 程序组合的成员描述。程序组合里含"未开"的程序（<see cref="Windows"/> 里没有）。
    /// null = 自动折叠组（按进程认领全部窗口）。
    /// </summary>
    public IReadOnlyList<MemberSpec>? Members { get; init; }

    /// <summary>
    /// 旧字段：解散单进程组时置位。现已由 <see cref="Members"/> 取代，仅为兼容旧布局保留。
    /// </summary>
    public bool NoAutoGroup { get; init; }

    /// <summary>手工创建的空程序组：没有成员也要显示在界面上。</summary>
    public bool IsEmptyGroup { get; init; }

    public WindowInfo? SingleWindow => Windows.Count > 0 ? Windows[0] : null;
    public int Count => Windows.Count;

    /// <summary>返回一个改了名字的副本。name 传 null 表示恢复默认名。</summary>
    public ResolvedSlot WithName(string? name) => new()
    {
        Kind = Kind,
        Name = string.IsNullOrWhiteSpace(name) ? DefaultName() : name!,
        CustomName = string.IsNullOrWhiteSpace(name) ? null : name,
        Windows = Windows,
        Children = Children,
        Processes = Processes,
        IsOverflow = IsOverflow,
        MemberOrder = MemberOrder,
        Members = RenameMembers(name),   // 必须带上：丢了它就退化成"该进程的全部窗口"
        NoAutoGroup = NoAutoGroup,
        IsEmptyGroup = IsEmptyGroup,
    };

    /// <summary>
    /// 程序（单成员）改名时，把显示名落到成员描述上——持久化写的是 Members，
    /// 不更新它的话重命名下次解析就丢了。
    /// </summary>
    private IReadOnlyList<MemberSpec>? RenameMembers(string? name)
    {
        if (Kind != SlotKind.Window || Members is not { Count: 1 }) return Members;
        var m = Members[0];
        return new[]
        {
            new MemberSpec(m.Process, m.Title)
            {
                DisplayName = string.IsNullOrWhiteSpace(name) ? null : name,
                ExePath = m.ExePath,
                Hwnd = m.Hwnd,
            }
        };
    }

    private string DefaultName() => Kind switch
    {
        SlotKind.Combination => LayoutResolver.AutoNameForCombination(Members),
        SlotKind.Group => LayoutResolver.AutoName(Windows),
        _ => SingleWindow?.Title ?? "",
    };
}

/// <summary>
/// 把「枚举到的窗口」+「持久化的布局」解析成实际的 16 槽位树。
///
/// 三种对象：
///   程序（Window）     —— 一个窗口，按下直接切
///   程序组（Group）     —— 按下进二级，里面是程序 / 程序组合
///   程序组合（Combination）—— 按下打开组内全部程序（已开激活，未开启动）
///
/// 规则（依次施加）：
///   1) 按 <see cref="LayoutDocument.Slots"/> 的顺序安置已知窗口
///   2) 布局里没提到的进程按 z-order 追加在后面
///   3) 同一进程窗口数 ≥ <c>autoGroupThreshold</c> 时自动折叠成一个组，占一个槽位
///   4) 只剩 1 个成员的**自动组**自动降级为窗口槽位；手工程序组即使为空也保留
///   5) 槽位超过 16 个时，第 16 格变成"更多…"溢出组，装下所有余量
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
        var exact = new Dictionary<MemberSpec, WindowInfo?>();
        ReserveExact(layout.Slots, remaining, exact);

        // ---- 1) 再按布局顺序组装。精确匹配没中的，才允许按进程兜底 ----
        foreach (var def in layout.Slots)
        {
            foreach (var s in ResolveDefinition(def, remaining, windows, autoGroupThreshold, exact))
                slots.Add(s);
        }

        // ---- 2) 布局里没提到的进程，按 z-order 追加 ----
        foreach (var group in remaining.GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var members = SortByZOrder(group.ToList(), windows);
            foreach (var s in AutoSlots(SlotKind.Window, members, autoGroupThreshold, null, null, null))
                slots.Add(s);
        }

        // ---- 3) 溢出处理 ----
        return ApplyOverflow(slots, pageSize);
    }

    // ============================================================
    //  解析：逐条 SlotDefinition
    // ============================================================

    private static IEnumerable<ResolvedSlot> ResolveDefinition(
        SlotDefinition def, List<WindowInfo> remaining,
        IReadOnlyList<WindowInfo> zorder, int autoGroupThreshold,
        Dictionary<MemberSpec, WindowInfo?> exact)
    {
        if (def.Kind == SlotKind.Combination)
        {
            var c = ResolveCombination(def, remaining, exact);
            if (c != null) yield return c;
            yield break;
        }

        // 手工程序组：有 Children（新格式）或 Members（旧格式的组，迁移成子项）
        if (def.Kind == SlotKind.Group && (def.Children != null || def.Members != null))
        {
            yield return ResolveManualGroup(def, remaining, zorder, autoGroupThreshold, exact);
            yield break;
        }

        // 程序 / 自动折叠组
        foreach (var s in ResolveFlat(def, remaining, zorder, autoGroupThreshold, exact))
            yield return s;
    }

    private static ResolvedSlot? ResolveCombination(
        SlotDefinition def, List<WindowInfo> remaining, Dictionary<MemberSpec, WindowInfo?> exact)
    {
        var refs = def.Members;
        if (refs is not { Count: > 0 }) return null;   // 空组合不显示

        // 只精确/兜底认领每个 ref 对应的窗口，**不吃同进程的其它窗口**。
        // 之前的"吃掉同进程所有剩余"会让多个组合共享进程时第一个独占（用户报的 bug）。
        // 现在 Members 一窗口一 ref，每个 ref 各取各的，remaining 自然被精确 Claim 清空，
        // 也就不会被追加循环吐回——bug3（"组合后 B 又出现"）同样不复现。
        var windows = new List<WindowInfo>();
        foreach (var spec in refs)
        {
            var w = Claim(spec, remaining, exact);
            if (w != null) windows.Add(w);
        }

        return new ResolvedSlot
        {
            Kind = SlotKind.Combination,
            Name = string.IsNullOrWhiteSpace(def.Name) ? AutoNameForCombination(refs) : def.Name!,
            CustomName = string.IsNullOrWhiteSpace(def.Name) ? null : def.Name,
            Windows = windows,
            Processes = refs.Select(r => r.Process).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Members = refs,   // 保留全部 refs（含未开的），按下时据此启动
        };
    }

    private static ResolvedSlot ResolveManualGroup(
        SlotDefinition def, List<WindowInfo> remaining,
        IReadOnlyList<WindowInfo> zorder, int autoGroupThreshold,
        Dictionary<MemberSpec, WindowInfo?> exact)
    {
        var children = new List<ResolvedSlot>();

        if (def.Children != null)
        {
            foreach (var childDef in def.Children)
            {
                foreach (var c in ResolveDefinition(childDef, remaining, zorder, autoGroupThreshold, exact))
                    children.Add(c);
            }
        }
        else if (def.Members is { Count: > 0 })
        {
            // 旧格式：Members 是扁平窗口列表，迁移成"每个窗口一个程序子项"
            foreach (var spec in def.Members)
            {
                var w = Claim(spec, remaining, exact);
                if (w == null) continue;
                children.Add(MakeWindow(w, spec.DisplayName, null));
            }
        }

        var windows = children.SelectMany(c => c.Windows).ToList();
        var procs = children.SelectMany(c => c.Processes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = string.IsNullOrWhiteSpace(def.Name) ? AutoName(windows) : def.Name!,
            CustomName = string.IsNullOrWhiteSpace(def.Name) ? null : def.Name,
            Children = children,
            Windows = windows,
            Processes = procs,
            IsEmptyGroup = children.Count == 0,
        };
    }

    /// <summary>程序 / 自动折叠组 / 旧格式的散装窗口。</summary>
    private static IEnumerable<ResolvedSlot> ResolveFlat(
        SlotDefinition def, List<WindowInfo> remaining,
        IReadOnlyList<WindowInfo> zorder, int autoGroupThreshold,
        Dictionary<MemberSpec, WindowInfo?> exact)
    {
        var taken = new List<WindowInfo>();
        // 与 taken 一一对应的 spec 列表：某个成员这轮没认领到窗口时，
        // 必须把它从 Members 里一起剔除，否则 Windows[i] 会和 Members[i] 错位。
        List<MemberSpec>? matchedSpecs = null;

        if (def.Members is { Count: > 0 })
        {
            matchedSpecs = new List<MemberSpec>(def.Members.Count);
            foreach (var spec in def.Members)
            {
                var w = Claim(spec, remaining, exact);
                if (w == null) continue;          // 这个成员没开，跳过它和它的 spec
                taken.Add(w);
                matchedSpecs.Add(spec);
            }
        }
        else
        {
            if (def.Processes.Count == 0) yield break;
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
            taken = SortByZOrder(taken, zorder);
        }

        if (taken.Count == 0) yield break;   // 这条布局对应的窗口这次一个都没开，丢弃

        // 旧字段兼容：解散单进程组 → 每窗口一个槽位
        if (def.NoAutoGroup && def.Members is null or { Count: 0 } && taken.Count > 1)
        {
            foreach (var w in ApplyMemberOrder(taken, def.MemberOrder))
                yield return MakeWindow(w, null, null);
            yield break;
        }

        foreach (var s in AutoSlots(def.Kind, taken, autoGroupThreshold, def.Name, matchedSpecs, def.MemberOrder))
            yield return s;
    }

    /// <summary>
    /// 决定一批窗口该做成一个自动折叠组，还是拆成独立的窗口槽位。
    /// 自动组没有 Children，用平铺的 <see cref="ResolvedSlot.Windows"/> 表示。
    /// </summary>
    private static IEnumerable<ResolvedSlot> AutoSlots(
        SlotKind declaredKind, List<WindowInfo> taken, int autoGroupThreshold,
        string? name, IReadOnlyList<MemberSpec>? specs, IReadOnlyList<string>? memberOrder)
    {
        bool isGroup = taken.Count > 1
            && (declaredKind == SlotKind.Group || taken.Count >= autoGroupThreshold);

        if (isGroup)
        {
            var ordered = specs is { Count: > 0 }
                ? taken                                    // 顺序由 Members 决定
                : ApplyMemberOrder(taken, memberOrder);    // 自动折叠组：按用户拖过的顺序
            yield return new ResolvedSlot
            {
                Kind = SlotKind.Group,
                Name = string.IsNullOrWhiteSpace(name) ? AutoName(ordered) : name!,
                CustomName = string.IsNullOrWhiteSpace(name) ? null : name,
                Windows = ordered,
                Processes = ordered.Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                MemberOrder = specs is { Count: > 0 } ? null : memberOrder,
                Members = specs,
            };
            yield break;
        }

        for (int i = 0; i < taken.Count; i++)
        {
            bool named = !string.IsNullOrWhiteSpace(name);
            string? display = specs is { Count: > 0 } && i < specs.Count ? specs[i].DisplayName : null;
            yield return MakeWindow(taken[i], display, named ? name : null);
        }
    }

    private static ResolvedSlot MakeWindow(WindowInfo w, string? display, string? customName)
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
            Members = new[] { new MemberSpec(w.ProcessName, w.Title) { DisplayName = display, Hwnd = (long)w.Hwnd } },
        };
    }

    // ============================================================
    //  认领 / 匹配
    // ============================================================

    /// <summary>
    /// 跨槽位预留匹配（含程序组的子项，递归）。分两轮，优先级从高到低：
    ///   1) 窗口句柄提示——同一次运行内最可靠，标题变了也能精确找回原窗口；
    ///   2) (进程, 标题) 精确——兜住首次运行 / 重启后句柄失效的情况。
    /// 必须整体先做：否则某个槽位的兜底会抢走后面槽位本该精确匹配的窗口。
    /// </summary>
    private static void ReserveExact(
        IEnumerable<SlotDefinition> defs, List<WindowInfo> remaining,
        Dictionary<MemberSpec, WindowInfo?> exact)
    {
        var specs = new List<MemberSpec>();
        CollectSpecs(defs, specs);

        foreach (var spec in specs)
        {
            var w = TakeHwnd(remaining, spec);
            if (w != null) exact[spec] = w;
        }

        foreach (var spec in specs)
        {
            if (exact.ContainsKey(spec)) continue;
            var w = TakeExact(remaining, spec);
            if (w != null) exact[spec] = w;
        }
    }

    private static void CollectSpecs(IEnumerable<SlotDefinition> defs, List<MemberSpec> specs)
    {
        foreach (var def in defs)
        {
            if (def.Children is { } children)
            {
                CollectSpecs(children, specs);
                continue;
            }
            if (def.Members is { Count: > 0 })
                specs.AddRange(def.Members);
        }
    }

    /// <summary>先吃预留下的匹配，没有再按进程"挑最像的"兜底。命中的窗口会回填句柄提示。</summary>
    private static WindowInfo? Claim(
        MemberSpec spec, List<WindowInfo> remaining, Dictionary<MemberSpec, WindowInfo?> exact)
    {
        WindowInfo? w = exact.TryGetValue(spec, out var e) && e != null
            ? e
            : TakeBestMatch(remaining, spec);

        // 回填句柄提示：同一次运行的后续解析据此精确认领（标题再变也不丢）。
        if (w != null && spec.Hwnd != (long)w.Hwnd) spec.Hwnd = (long)w.Hwnd;
        return w;
    }

    /// <summary>按窗口句柄提示精确认领一个窗口（进程名必须也匹配，防止句柄被回收后张冠李戴）。</summary>
    private static WindowInfo? TakeHwnd(List<WindowInfo> remaining, MemberSpec spec)
    {
        if (spec.Hwnd is not long h || h == 0) return null;
        var hwnd = new IntPtr(h);
        for (int i = 0; i < remaining.Count; i++)
        {
            if (remaining[i].Hwnd == hwnd
                && string.Equals(remaining[i].ProcessName, spec.Process, StringComparison.OrdinalIgnoreCase))
            {
                var w = remaining[i];
                remaining.RemoveAt(i);
                return w;
            }
        }
        return null;
    }

    /// <summary>按 (进程, 标题) 精确认领一个窗口，并从待分配里移除。</summary>
    private static WindowInfo? TakeExact(List<WindowInfo> remaining, MemberSpec spec)
    {
        if (string.IsNullOrEmpty(spec.Title)) return null;
        // 从最旧的一端扫：标题完全相同的多个窗口里优先认领更早存在的那个，
        // 把新开的同标题窗口留给"多余窗口"处理，别让它挤掉原成员。
        for (int i = remaining.Count - 1; i >= 0; i--)
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
    /// 标题对不上时的兜底：在该进程的剩余窗口里挑"最像"的那个，而不是无脑拿最近激活的。
    ///
    /// 之前直接取 z-order 最前（最近激活）的窗口，于是新开的同进程窗口会把组合里
    /// 原来的窗口挤出去（用户报的 bug）。现在按标题的**最长公共后缀**打分
    /// （VS Code / 浏览器换文件时后缀里的目录名不变），分数相同时取最旧的那个，
    /// 让新窗口优先留在组合外面。
    /// </summary>
    private static WindowInfo? TakeBestMatch(List<WindowInfo> remaining, MemberSpec spec)
    {
        int bestIndex = -1;
        int bestScore = -1;
        // 从尾部（最旧）往前扫：同分时先遇到的（更旧）胜出
        for (int i = remaining.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(remaining[i].ProcessName, spec.Process, StringComparison.OrdinalIgnoreCase))
                continue;
            int score = string.IsNullOrEmpty(spec.Title)
                ? 0
                : CommonSuffixLength(spec.Title, remaining[i].Title);
            if (score > bestScore) { bestScore = score; bestIndex = i; }
        }
        if (bestIndex < 0) return null;
        var w = remaining[bestIndex];
        remaining.RemoveAt(bestIndex);
        return w;
    }

    /// <summary>两个标题从末尾起相同的字符数（忽略大小写）。</summary>
    private static int CommonSuffixLength(string a, string b)
    {
        int i = a.Length - 1, j = b.Length - 1, n = 0;
        while (i >= 0 && j >= 0 && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j]))
        {
            n++; i--; j--;
        }
        return n;
    }

    /// <summary>按窗口在原始 z-order 列表中的位置排序。</summary>
    private static List<WindowInfo> SortByZOrder(List<WindowInfo> subset, IReadOnlyList<WindowInfo> zorder)
    {
        var rank = new Dictionary<IntPtr, int>();
        for (int i = 0; i < zorder.Count; i++) rank[zorder[i].Hwnd] = i;
        return subset.OrderBy(w => rank.TryGetValue(w.Hwnd, out int r) ? r : int.MaxValue).ToList();
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

    // ============================================================
    //  命名
    // ============================================================

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

    /// <summary>程序组合的自动名：按进程集合生成。</summary>
    public static string AutoNameForCombination(IReadOnlyList<MemberSpec>? members)
    {
        if (members is not { Count: > 0 }) return "组合";
        var distinct = members.Select(m => m.Process)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();
        var head = StripExe(distinct[0]);
        return distinct.Count == 1 ? head : $"{head} +{distinct.Count - 1}";
    }

    private static string StripExe(string proc) =>
        proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? proc[..^4]
            : proc;

    // ============================================================
    //  溢出
    // ============================================================

    /// <summary>槽位多于一页时，最后一格改成"更多…"组装下余量（平铺成窗口）。</summary>
    private static IReadOnlyList<ResolvedSlot> ApplyOverflow(List<ResolvedSlot> slots, int pageSize)
    {
        if (pageSize <= 0 || slots.Count <= pageSize) return slots;

        var head = slots.Take(pageSize - 1).ToList();
        var tail = slots.Skip(pageSize - 1).ToList();

        var overflowWindows = tail.SelectMany(s => s.Windows).ToList();
        var overflowProcs = tail.SelectMany(s => s.Processes)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
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
            yield return new MemberSpec(w.ProcessName, w.Title) { DisplayName = display, Hwnd = (long)w.Hwnd };
        }
    }

    // ============================================================
    //  持久化
    // ============================================================

    /// <summary>把当前槽位顺序写成可持久化的布局（溢出组会被摊平）。</summary>
    public static LayoutDocument ToDocument(IReadOnlyList<ResolvedSlot> slots)
    {
        var doc = new LayoutDocument();
        foreach (var s in slots)
            AddDefinition(doc.Slots, s);
        return doc;
    }

    private static void AddDefinition(List<SlotDefinition> list, ResolvedSlot s)
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
                list.Add(new SlotDefinition
                {
                    Kind = SlotKind.Window,
                    Name = display,
                    Processes = new List<string> { w.ProcessName },
                    Members = new List<MemberSpec> { new(w.ProcessName, w.Title) { DisplayName = display, Hwnd = (long)w.Hwnd } },
                });
            }
            return;
        }

        // 手工程序组：子项（程序 / 程序组合）递归写
        if (s.Children != null)
        {
            var def = new SlotDefinition
            {
                Kind = SlotKind.Group,
                Name = s.CustomName,
                Children = new List<SlotDefinition>(),
            };
            foreach (var c in s.Children)
                AddDefinition(def.Children, c);
            list.Add(def);
            return;
        }

        // 程序 / 程序组合：精确到窗口的成员
        if (s.Members is { Count: > 0 })
        {
            list.Add(new SlotDefinition
            {
                Kind = s.Kind,
                Name = s.CustomName,
                Processes = s.Processes.ToList(),
                Members = s.Members.ToList(),
            });
            return;
        }

        // 自动折叠组：按进程 + 顺序提示
        list.Add(new SlotDefinition
        {
            Kind = s.Kind,
            // 只有用户改过的名字才落盘；没改过的下次解析时重新推导
            Name = s.CustomName,
            Processes = s.Processes.ToList(),
            MemberOrder = s.MemberOrder?.ToList(),
        });
    }
}
