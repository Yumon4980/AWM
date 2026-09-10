using System.Collections.Generic;

namespace AltTabReplacer.Core;

/// <summary>
/// 物理键（QWERTY 位置）→ 本地索引 0..34 的共享映射。
/// 用于：
///   1) SelectorWindow 渲染 cell 标签 + 处理 WPF KeyDown
///   2) App 的全局低层键盘钩子判断哪些 VK 该拦截
///
/// 数字行：1 2 3 4 5 6 7 8 9   (VK 0x31..0x39)  → 索引 0..8
/// Q 行：   Q W E R T Y U I O P (VK 0x51..0x50)  → 索引 9..18
/// A 行：   A S D F G H J K L   (VK 0x41,0x53,0x44,0x46,0x47,0x48,0x4A,0x4B,0x4C) → 索引 19..27
/// Z 行：   Z X C V B N M       (VK 0x5A,0x58,0x43,0x56,0x42,0x4E,0x4D) → 索引 28..34
/// </summary>
public static class KeyMap
{
    /// <summary>本地索引 → Win32 VirtualKey 码（用于反查 WPF Key）。</summary>
    public static readonly int[] IndexToVk = new[]
    {
        0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,                  // 1..9
        0x51, 0x57, 0x45, 0x52, 0x54, 0x59, 0x55, 0x49, 0x4F, 0x50,            // Q W E R T Y U I O P
        0x41, 0x53, 0x44, 0x46, 0x47, 0x48, 0x4A, 0x4B, 0x4C,                  // A S D F G H J K L
        0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D,                                // Z X C V B N M
    };

    /// <summary>本地索引 → 显示用的字符串标签。</summary>
    public static readonly string[] IndexToLabel = new[]
    {
        "1","2","3","4","5","6","7","8","9",
        "Q","W","E","R","T","Y","U","I","O","P",
        "A","S","D","F","G","H","J","K","L",
        "Z","X","C","V","B","N","M",
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
}
