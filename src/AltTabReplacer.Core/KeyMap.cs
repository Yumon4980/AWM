using System.Collections.Generic;

namespace AltTabReplacer.Core;

/// <summary>
/// 物理键（QWERTY 位置）→ 本地索引 0..15 的共享映射。
///
/// 用左手主键区一个完整的 4×4 方块，物理位置和界面网格一一对应，肌肉记忆成本最低：
///
///     1 2 3 4     → 索引 0..3
///     Q W E R     → 索引 4..7
///     A S D F     → 索引 8..11
///     Z X C V     → 索引 12..15
///
/// 两级结构下容量是 16 × 16 = 256 个窗口，远超实际需要。
///
/// 用于：
///   1) SelectorWindow 渲染槽位标签 + 处理 WPF KeyDown
///   2) App 的全局低层键盘钩子判断哪些 VK 该拦截
/// </summary>
public static class KeyMap
{
    /// <summary>一页的槽位数。</summary>
    public const int Size = 16;

    /// <summary>视觉网格的行列数（4×4），与物理键位一致。</summary>
    public const int Rows = 4;
    public const int Cols = 4;

    /// <summary>本地索引 → Win32 VirtualKey 码。</summary>
    public static readonly int[] IndexToVk = new[]
    {
        0x31, 0x32, 0x33, 0x34,     // 1 2 3 4
        0x51, 0x57, 0x45, 0x52,     // Q W E R
        0x41, 0x53, 0x44, 0x46,     // A S D F
        0x5A, 0x58, 0x43, 0x56,     // Z X C V
    };

    /// <summary>本地索引 → 显示用的字符串标签。</summary>
    public static readonly string[] IndexToLabel = new[]
    {
        "1", "2", "3", "4",
        "Q", "W", "E", "R",
        "A", "S", "D", "F",
        "Z", "X", "C", "V",
    };

    private static readonly Dictionary<int, int> _vkToIndex;

    static KeyMap()
    {
        _vkToIndex = new Dictionary<int, int>(IndexToVk.Length);
        for (int i = 0; i < IndexToVk.Length; i++)
        {
            _vkToIndex[IndexToVk[i]] = i;
        }
    }

    /// <summary>把 Win32 VirtualKey 转换为本地索引；不命中返回 null。</summary>
    public static int? ToIndex(int vk) => _vkToIndex.TryGetValue(vk, out int i) ? i : null;

    /// <summary>索引 → 标签，越界返回 "?"。</summary>
    public static string LabelOf(int index) =>
        index >= 0 && index < IndexToLabel.Length ? IndexToLabel[index] : "?";
}
