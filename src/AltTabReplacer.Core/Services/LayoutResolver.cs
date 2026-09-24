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

    /// <summary>
    /// 整槽位的启动兜底路径（锁定槽位窗口全关后重新启动用）。
    /// 有 Members 的槽位优先用 <see cref="MemberSpec.ExePath"/>；这条是按进程语义的
    /// 窗口 / 自动折叠组的兜底，来自 <see cref="SlotDefinition.ExePath"/>。
    /// </summary>
    public string? LaunchPath { get; init; }

    /// <summary>
    /// 整槽位级的启动命令行参数（同进程下需要 args 区分窗口，比如 explorer 路径窗口）。
    /// 优先用成员 <see cref="MemberSpec.LaunchArgs"/>；这是按进程语义组的兜底。
    /// </summary>
    public string? LaunchArgs { get; init; }

    /// <summary>手工创建的空程序组：没有成员也要显示在界面上。</summary>
    public bool IsEmptyGroup { get; init; }

    /// <summary>
    /// 窗口是否已经全部关闭（锁定槽位的"未运行"态）。
    /// 已关闭的成员用 Hwnd=0 的占位窗口表示——槽位还在网格上，按键时重新启动。
    /// </summary>
    public bool IsClosed => Windows.Count == 0 || Windows.All(w => w.Hwnd == IntPtr.Zero);

    /// <summary>锁定：固定占住 <see cref="Position"/> 键位，排序 / 删除都不移动它。</summary>
    public bool Locked { get; init; }

    /// <summary>显式键位索引（0..15）。null = 自动往前填。</summary>
    public int? Position { get; set; }

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
        Locked = Locked,
        Position = Position,
        LaunchPath = LaunchPath,
        LaunchArgs = LaunchArgs,
    };

    /// <summary>
    /// 返回一个改了锁定状态的副本。键位由调用方决定 —— 切换锁定时传当前位置，
    /// 这样锁定/解锁都不会让本格或其它格挪位。
    /// </summary>
    public ResolvedSlot WithLock(bool locked, int? position) => new()
    {
        Kind = Kind,
        Name = Name,
        CustomName = CustomName,
        Windows = Windows,
        Children = Children,
        Processes = Processes,
        IsOverflow = IsOverflow,
        MemberOrder = MemberOrder,
        Members = Members,
        NoAutoGroup = NoAutoGroup,
        IsEmptyGroup = IsEmptyGroup,
        Locked = locked,
        Position = position,
        LaunchPath = LaunchPath,
        LaunchArgs = LaunchArgs,
    };

    /// <summary>替换窗口列表的副本（其余字段原样保留）。锁定槽位的窗口关闭后就地变"未运行"时用。</summary>
    public ResolvedSlot WithWindows(IReadOnlyList<WindowInfo> windows) => new()
    {
        Kind = Kind,
        Name = Name,
        CustomName = CustomName,
        Windows = windows,
        Children = Children,
        Processes = Processes,
        IsOverflow = IsOverflow,
        MemberOrder = MemberOrder,
        Members = Members,
        NoAutoGroup = NoAutoGroup,
        IsEmptyGroup = IsEmptyGroup,
        Locked = Locked,
        Position = Position,
        LaunchPath = LaunchPath,
        LaunchArgs = LaunchArgs,
    };

    /// <summary>
    /// 替换子项列表的副本（手工程序组）。展平窗口 / 进程集合随子项重算，
    /// 语义与 <see cref="SlotEditor"/> 的组重建保持一致。
    /// </summary>
    public ResolvedSlot WithChildren(IReadOnlyList<ResolvedSlot> children) => new()
    {
        Kind = Kind,
        Name = Name,
        CustomName = CustomName,
        Children = children,
        Windows = children.SelectMany(c => c.Windows).ToList(),
        Processes = children.SelectMany(c => c.Processes)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        IsOverflow = IsOverflow,
        MemberOrder = MemberOrder,
        Members = Members,
        NoAutoGroup = NoAutoGroup,
        IsEmptyGroup = children.Count == 0,
        Locked = Locked,
        Position = Position,
        LaunchPath = LaunchPath,
        LaunchArgs = LaunchArgs,
    };

    /// <summary>替换启动兜底路径 / 成员描述的副本（锁定时记录"未开则启动"的路径用）。</summary>
    public ResolvedSlot WithLaunch(string? launchPath, IReadOnlyList<MemberSpec>? members = null,
        string? launchArgs = null) => new()
    {
        Kind = Kind,
        Name = Name,
        CustomName = CustomName,
        Windows = Windows,
        Children = Children,
        Processes = Processes,
        IsOverflow = IsOverflow,
        MemberOrder = MemberOrder,
        Members = members ?? Members,
        NoAutoGroup = NoAutoGroup,
        IsEmptyGroup = IsEmptyGroup,
        Locked = Locked,
        Position = Position,
        LaunchPath = launchPath,
        LaunchArgs = launchArgs ?? LaunchArgs,
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
                LaunchArgs = m.LaunchArgs,
                Hwnd = m.Hwnd,
                Locked = m.Locked,
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
        int autoGroupThreshold = 2)
    {
        // 不再在"无窗口"时提前返回：锁定槽位的窗口全关后也要解析出来（未运行态），
        // 否则窗口全关后连选择器都唤不出来了。

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

        // ---- 2) 布局里没提到的窗口，按 z-order 各自占一个槽位 ----
        // 不自动折叠：新开 / 新出现的程序各占一格，不会被悄悄并进某个自动组。
        // 需要成组时由用户手动拖成"程序组 / 程序组合"。
        foreach (var w in remaining)
            slots.Add(MakeWindow(w, null, null));

        // 溢出不在这里折叠：一页（16 键）放不下的槽位由 UI 放进"未入网格"列表，
        // 而不是挤成一个"更多…"组——那样会把 16 号键也占掉。
        return slots;
    }

    // ============================================================
    //  解析：逐条 SlotDefinition
    // ============================================================

    private static IEnumerable<ResolvedSlot> ResolveDefinition(
        SlotDefinition def, List<WindowInfo> remaining,
        IReadOnlyList<WindowInfo> zorder, int autoGroupThreshold,
        Dictionary<MemberSpec, WindowInfo?> exact,
        bool inheritLock = false)
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
            yield return ResolveManualGroup(def, remaining, zorder, autoGroupThreshold, exact, inheritLock);
            yield break;
        }

        // 程序 / 自动折叠组
        foreach (var s in ResolveFlat(def, remaining, zorder, autoGroupThreshold, exact, inheritLock))
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
            Locked = def.Locked,
            Position = def.Position,
            LaunchPath = def.ExePath,
            LaunchArgs = def.LaunchArgs,
        };
    }

    private static ResolvedSlot ResolveManualGroup(
        SlotDefinition def, List<WindowInfo> remaining,
        IReadOnlyList<WindowInfo> zorder, int autoGroupThreshold,
        Dictionary<MemberSpec, WindowInfo?> exact, bool inheritLock)
    {
        var children = new List<ResolvedSlot>();

        if (def.Children != null)
        {
            foreach (var childDef in def.Children)
            {
                // 组被锁定 = 整个结构都要保留：成员窗口关掉后组内也保留"未运行"占位
                foreach (var c in ResolveDefinition(childDef, remaining, zorder, autoGroupThreshold, exact,
                             inheritLock: inheritLock || def.Locked))
                    children.Add(c);
            }
        }
        else if (def.Members is { Count: > 0 })
        {
            // 旧格式：Members 是扁平窗口列表，迁移成"每个窗口一个程序子项"
            bool strict = def.Locked || inheritLock;
            foreach (var spec in def.Members)
            {
                var w = Claim(spec, remaining, exact, strict: strict || spec.Locked);
                if (w == null)
                {
                    if (def.Locked || inheritLock || spec.Locked)
                        children.Add(MakeGhostWindow(spec, spec.DisplayName, locked: def.Locked || spec.Locked,
                            position: null, launchPath: spec.ExePath));
                    continue;
                }
                children.Add(MakeWindow(w, spec.DisplayName, null, spec: spec));
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
            Locked = def.Locked,
            Position = def.Position,
            LaunchPath = def.ExePath,
            LaunchArgs = def.LaunchArgs,
        };
    }

    /// <summary>程序 / 自动折叠组 / 旧格式的散装窗口。</summary>
    private static IEnumerable<ResolvedSlot> ResolveFlat(
        SlotDefinition def, List<WindowInfo> remaining,
        IReadOnlyList<WindowInfo> zorder, int autoGroupThreshold,
        Dictionary<MemberSpec, WindowInfo?> exact, bool inheritLock)
    {
        var taken = new List<WindowInfo>();
        // 与 taken 一一对应的 spec 列表：某个成员这轮没认领到窗口时，
        // 必须把它从 Members 里一起剔除，否则 Windows[i] 会和 Members[i] 错位。
        List<MemberSpec>? matchedSpecs = null;

        if (def.Members is { Count: > 0 })
        {
            matchedSpecs = new List<MemberSpec>(def.Members.Count);
            // 锁定槽位 / 锁定组：成员严格认领，不走"按进程挑最像"的兜底——
            // 否则同进程的无关窗口会被吸进槽位（explorer 主页抢路径窗口、Chrome 新标签抢旧标签等）。
            bool strict = def.Locked || inheritLock;
            foreach (var spec in def.Members)
            {
                var w = Claim(spec, remaining, exact, strict: strict || spec.Locked);
                if (w != null)
                {
                    taken.Add(w);
                    matchedSpecs.Add(spec);
                }
                else if (def.Locked || inheritLock || spec.Locked)
                {
                    // 锁定的成员窗口关掉后不消失：用 Hwnd=0 的占位窗口留在原位，
                    // 按键时按记录的可执行路径重新启动。
                    taken.Add(GhostWindow(spec));
                    matchedSpecs.Add(spec);
                }
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

        if (taken.Count == 0)
        {
            // 这条布局对应的窗口这次一个都没开。锁定的槽位仍然保留（未运行态），
            // 否则锁定键位会随着程序关闭而消失。
            if ((def.Locked || inheritLock) && def.Processes.Count > 0)
            {
                yield return MakeGhostWindow(
                    new MemberSpec(def.Processes[0], null), def.Name,
                    locked: def.Locked, position: def.Position, launchPath: def.ExePath);
            }
            yield break;
        }

        // 旧字段兼容：解散单进程组 → 每窗口一个槽位
        if (def.NoAutoGroup && def.Members is null or { Count: 0 } && taken.Count > 1)
        {
            foreach (var w in ApplyMemberOrder(taken, def.MemberOrder))
                yield return MakeWindow(w, null, null);
            yield break;
        }

        foreach (var s in AutoSlots(def.Kind, taken, autoGroupThreshold, def.Name, matchedSpecs,
                     def.MemberOrder, def.Locked, def.Position, def.ExePath))
            yield return s;
    }

    /// <summary>
    /// 决定一批窗口该做成一个自动折叠组，还是拆成独立的窗口槽位。
    /// 自动组没有 Children，用平铺的 <see cref="ResolvedSlot.Windows"/> 表示。
    /// 列表里可能混有 Hwnd=0 的"未运行"占位窗口（锁定成员已关闭），原样带过去。
    /// </summary>
    private static IEnumerable<ResolvedSlot> AutoSlots(
        SlotKind declaredKind, List<WindowInfo> taken, int autoGroupThreshold,
        string? name, IReadOnlyList<MemberSpec>? specs, IReadOnlyList<string>? memberOrder,
        bool locked = false, int? Position = null, string? launchPath = null)
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
                Locked = locked,
                Position = Position,
                LaunchPath = launchPath,
                LaunchArgs = specs is { Count: > 0 } ? specs[0].LaunchArgs : null,
            };
            yield break;
        }

        for (int i = 0; i < taken.Count; i++)
        {
            bool named = !string.IsNullOrWhiteSpace(name);
            var spec = specs is { Count: > 0 } && i < specs.Count ? specs[i] : null;
            string? display = spec?.DisplayName;
            yield return MakeWindow(taken[i], display, named ? name : null, locked, Position, spec, launchPath);
        }
    }

    private static ResolvedSlot MakeWindow(WindowInfo w, string? display, string? customName,
        bool locked = false, int? Position = null,
        MemberSpec? spec = null, string? launchPath = null)
    {
        string? effectiveCustom = !string.IsNullOrWhiteSpace(display) ? display : customName;
        // 带上 spec 的既有信息（ExePath / 启动参数 / 锁定标记），否则一次"解析→落盘"循环就把
        // 锁定时记录的可执行路径冲掉了。窗口标题存在时以窗口实际标题为准；spec.Title 是"原始目标"
        // 标题（ex: explorer 路径），落盘后用来跨次唤起精确认领原窗口，必须保留。
        // explorer 路径窗口的标题就是路径；首次解析时把它记成 LaunchArgs，
        // 这样锁定后关闭窗口，按键能 `explorer.exe "<原路径>"` 精确重启。
        string? explorerArgs = IsExplorer(w.ProcessName) ? GetExplorerPathArgs(w.Title) : null;
        string memberTitle = spec != null && !string.IsNullOrEmpty(spec.Title)
            ? spec.Title
            : w.Title;
        var member = spec != null
            ? new MemberSpec(spec.Process, memberTitle)
            {
                DisplayName = display ?? spec.DisplayName,
                ExePath = spec.ExePath,
                LaunchArgs = spec.LaunchArgs ?? explorerArgs,
                Hwnd = (long)w.Hwnd,
                Locked = spec.Locked,
            }
            : new MemberSpec(w.ProcessName, w.Title)
            {
                DisplayName = display,
                Hwnd = (long)w.Hwnd,
                ExePath = launchPath,
                LaunchArgs = explorerArgs,
            };
        return new ResolvedSlot
        {
            Kind = SlotKind.Window,
            // 名字用 spec 原始标题（ex: "D:\Projects"），不写"主页"——避免再次解析时被覆盖
            Name = !string.IsNullOrWhiteSpace(display) ? display!
                : !string.IsNullOrWhiteSpace(customName) ? customName!
                : (spec != null && !string.IsNullOrEmpty(spec.Title) ? spec.Title : w.Title),
            CustomName = effectiveCustom,
            Windows = new[] { w },
            Processes = new[] { w.ProcessName },
            Members = new[] { member },
            Locked = locked,
            Position = Position,
            LaunchPath = launchPath,
            LaunchArgs = member.LaunchArgs,
        };
    }

    // ============================================================
    //  未运行占位（锁定槽位的窗口全关后仍然保留在键位上）
    // ============================================================

    /// <summary>已关闭成员的占位窗口：Hwnd=0，标题取显示名 / 原标题 / 进程名。</summary>
    private static WindowInfo GhostWindow(MemberSpec spec)
    {
        string title = !string.IsNullOrWhiteSpace(spec.DisplayName) ? spec.DisplayName
            : !string.IsNullOrWhiteSpace(spec.Title) ? spec.Title
            : StripExe(spec.Process);
        return new WindowInfo(IntPtr.Zero, title, spec.Process, 0, false);
    }

    /// <summary>一个"未运行"的程序槽位：窗口列表里只有占位窗口，按下时按 ExePath / LaunchPath 重启。</summary>
    private static ResolvedSlot MakeGhostWindow(
        MemberSpec spec, string? name, bool locked, int? position, string? launchPath)
    {
        var ghost = GhostWindow(spec);
        string display = !string.IsNullOrWhiteSpace(name) ? name! : ghost.Title;
        return new ResolvedSlot
        {
            Kind = SlotKind.Window,
            Name = display,
            CustomName = name,
            Windows = new[] { ghost },
            Processes = new[] { spec.Process },
            Members = new[] { spec },
            Locked = locked,
            Position = position,
            LaunchPath = launchPath ?? spec.ExePath,
            LaunchArgs = spec.LaunchArgs,
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
        MemberSpec spec, List<WindowInfo> remaining, Dictionary<MemberSpec, WindowInfo?> exact,
        bool strict = false)
    {
        // strict=true（来自锁定槽位 / 锁定组）时不走"按进程挑最像"的兜底：
        // 兜底会把同进程里无关窗口（explorer 主页 / 新打开的标签页等）吸进槽位，
        // 破坏"一个槽位对应一个具体窗口"的语义。锁定只精确 / 按句柄认领。
        WindowInfo? w = exact.TryGetValue(spec, out var e) && e != null
            ? e
            : strict ? StrictClaim(remaining, spec) : TakeBestMatch(remaining, spec);

        // 回填句柄提示：同一次运行的后续解析据此精确认领（标题再变也不丢）。
        if (w != null && spec.Hwnd != (long)w.Hwnd) spec.Hwnd = (long)w.Hwnd;
        return w;
    }

    /// <summary>
    /// 锁定成员的"标题变了"兜底。浏览器等应用的窗口标题随内容变化，程序重启后 hwnd 提示
    /// （按设计不落盘）失效、标题精确匹配也失败，之前直接判"已关闭"变成占位，真窗口却被
    /// 当成新窗口另立一格（用户实测：zen 浏览器换标签页后锁定槽位变已关闭 + 多出一个新槽）。
    ///
    /// 现在按"标题公共后缀 &gt; 0"认领同进程窗口——同一窗口改名的典型特征是旧标题为现标题的
    /// 尾部（'Zen Browser' → '新标签 — Zen Browser'）。后缀为 0 的一律不认，保持 strict 的
    /// 防误吸收语义：真窗口确实已关时，不把同进程无关窗口（explorer 主页等）吸进槽位。
    /// </summary>
    private static WindowInfo? StrictClaim(List<WindowInfo> remaining, MemberSpec spec)
    {
        int best = -1, bestScore = 0;
        for (int i = remaining.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(remaining[i].ProcessName, spec.Process, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(spec.Title)) continue;   // 没有可比较的标题提示，保持占位
            int score = CommonSuffixLength(spec.Title, remaining[i].Title);
            if (score > bestScore) { bestScore = score; best = i; }
        }

        if (best < 0 || bestScore <= 0) return null;
        var w = remaining[best];
        remaining.RemoveAt(best);
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

    /// <summary>
    /// 从 explorer 窗口标题反查它实际打开的路径。
    /// explorer 路径窗口的窗口标题 = 完整路径（或"路径 - 文件名"），是窗口本身的标题。
    /// 主页（"主页"/"Desktop"）等不是路径，原样返回 null。
    ///
    /// **注意**：explorer 对长路径会自动在中间插入 `...` 截断（如 `D:\Pro...\sub`），
    /// 这种情况下函数返回 null——截断的路径传给 explorer.exe 无法恢复精确位置，
    /// 必须在用户右键"设置启动参数"里手动填写完整路径。
    /// </summary>
    public static string? GetExplorerPathArgs(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (title == "主页" || title.Equals("Desktop", StringComparison.OrdinalIgnoreCase)) return null;
        // 形如 "D:\foo\bar - 子目录" 的，按第一个 " - " 截取前段作为路径
        int sep = title.IndexOf(" - ", StringComparison.Ordinal);
        string path = sep > 0 ? title[..sep] : title;
        // 截断形式（含 `...`）不可用：explorer 命令行不会展开它
        if (path.Contains("...", StringComparison.Ordinal)) return null;
        // 必须看起来像路径（含盘符 + 冒号，或 UNC \\...），否则不算
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':') return path;
        if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\') return path;
        return null;
    }

    /// <summary>从进程名判断是不是资源管理器（explorer.exe）。</summary>
    public static bool IsExplorer(string processName) =>
        string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "explorer.exe", StringComparison.OrdinalIgnoreCase);

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
                Locked = s.Locked,
                Position = s.Position,
                ExePath = s.LaunchPath,
                LaunchArgs = s.LaunchArgs,
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
                Locked = s.Locked,
                Position = s.Position,
                ExePath = s.LaunchPath,
                LaunchArgs = s.LaunchArgs,
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
            Locked = s.Locked,
            Position = s.Position,
            ExePath = s.LaunchPath,
        });
    }
}
