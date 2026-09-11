using System;
using System.Collections.Generic;

namespace AltTabReplacer.Core.Services;

/// <summary>落点在行内的垂直分区。</summary>
public enum DropZone
{
    None = 0,
    InsertBefore = 1,
    InsertAfter = 2,
    IntoSlot = 3,
}

/// <summary>
/// 拖动落点判定。抽成纯函数是为了能测——之前这块逻辑埋在 WPF 事件里，
/// "行与行之间的缝判不到"这种问题只能靠手测，很容易漏。
/// </summary>
public static class DropHitTest
{
    /// <summary>
    /// 找出光标落在第几行、以及在该行内的相对位置。
    ///
    /// 判法是"第一个底边在光标之下的行"，而不是"落在行的上下边界之间"：
    /// ListBoxItem 之间有 Margin，逐行比较会把这些缝判成没命中，
    /// 然后掉进"追加到末尾"的兜底分支——用户看到的就是"拖了但顺序没变/莫名跑到最后"。
    /// 用底边判法，缝自然归到下一行的顶部。
    /// </summary>
    /// <param name="rows">各行的 (上边, 下边)，按屏幕坐标、从上到下。</param>
    /// <returns>命中行索引与行内相对位置(0~1)；rows 为空返回 (-1, 0)。</returns>
    public static (int Index, double Relative) Locate(IReadOnlyList<(double Top, double Bottom)> rows, double y)
    {
        if (rows.Count == 0) return (-1, 0);

        for (int i = 0; i < rows.Count; i++)
        {
            double h = rows[i].Bottom - rows[i].Top;
            if (h <= 0) continue;
            if (y >= rows[i].Bottom) continue;      // 光标还在本行下方，看下一行
            double rel = Math.Clamp((y - rows[i].Top) / h, 0, 1);
            return (i, rel);
        }

        // 在所有行下方 → 追加到末尾
        return (rows.Count - 1, 1.0);
    }

    /// <summary>
    /// 把"行索引 + 相对位置"翻译成落点语义。
    ///
    /// 上 25% → 插到本行之前；下 25% → 插到本行之后；中间 50% → 并入本行。
    /// 二级（组内）只允许排序，<paramref name="allowGrouping"/> 传 false，
    /// 中间区域按上下各半处理，禁止再嵌套分组。
    /// </summary>
    public static (int Index, DropZone Zone) Decide(
        int index, double relative, bool allowGrouping, bool forceOrder, bool forceGroup)
    {
        if (index < 0) return (-1, DropZone.None);

        // 二级：只有排序，中间一刀切
        if (!allowGrouping)
            return (index, relative < 0.5 ? DropZone.InsertBefore : DropZone.InsertAfter);

        if (forceGroup) return (index, DropZone.IntoSlot);
        if (forceOrder) return (index, relative < 0.5 ? DropZone.InsertBefore : DropZone.InsertAfter);
        if (relative < 0.25) return (index, DropZone.InsertBefore);
        if (relative > 0.75) return (index, DropZone.InsertAfter);
        return (index, DropZone.IntoSlot);
    }
}
