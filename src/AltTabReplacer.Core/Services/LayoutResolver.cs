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

        // ---- 1) 先按已保存的布局安置 ----
        foreach (var def in layout.Slots)
        {
            if (def.Processes.Count == 0) continue;

            var taken = new List<WindowInfo>();
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
            if (taken.Count == 0) continue;   // 这条布局对应的进程这次一个都没开，丢弃

            // 恢复 z-order（上面倒序遍历是为了安全删除）
            taken = SortByZOrder(taken, windows);
            slots.AddRange(MakeSlots(def.Kind, def.Name, def.Processes, taken, autoGroupThreshold));
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
        List<WindowInfo> members, int autoGroupThreshold)
    {
        bool isGroup = members.Count > 1
            && (declaredKind == SlotKind.Group || members.Count >= autoGroupThreshold);

        if (isGroup)
        {
            yield return new ResolvedSlot
            {
                Kind = SlotKind.Group,
                Name = string.IsNullOrWhiteSpace(name) ? AutoName(members) : name!,
                CustomName = string.IsNullOrWhiteSpace(name) ? null : name,
                Windows = members,
                Processes = processes.ToArray(),
            };
            yield break;
        }

        foreach (var w in members)
        {
            // 用户改过名就用用户的，否则用窗口标题
            bool named = !string.IsNullOrWhiteSpace(name);
            yield return new ResolvedSlot
            {
                Kind = SlotKind.Window,
                Name = named ? name! : w.Title,
                CustomName = named ? name : null,
                Windows = new[] { w },
                Processes = new[] { w.ProcessName },
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

        head.Add(new ResolvedSlot
        {
            Kind = SlotKind.Group,
            Name = $"更多… ({overflowWindows.Count})",
            Windows = overflowWindows,
            Processes = overflowProcs,
            IsOverflow = true,
        });
        return head;
    }

    /// <summary>把当前槽位顺序写成可持久化的布局（溢出组会被摊平）。</summary>
    public static LayoutDocument ToDocument(IReadOnlyList<ResolvedSlot> slots)
    {
        var doc = new LayoutDocument();
        foreach (var s in slots)
        {
            if (s.IsOverflow)
            {
                // 溢出组只是运行时容器，摊平成各自独立的槽位，别把它固化下来
                foreach (var proc in s.Processes)
                {
                    doc.Slots.Add(new SlotDefinition
                    {
                        Kind = SlotKind.Window,
                        Processes = new List<string> { proc },
                    });
                }
                continue;
            }

            doc.Slots.Add(new SlotDefinition
            {
                Kind = s.Kind,
                // 只有用户改过的名字才落盘；没改过的下次解析时重新推导（窗口标题会变，存了反而会过期）
                Name = s.CustomName,
                Processes = s.Processes.ToList(),
            });
        }
        return doc;
    }
}
