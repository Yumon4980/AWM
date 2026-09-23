using System.Collections.Generic;

namespace AltTabReplacer.Core;

/// <summary>
/// 物理键 → 本地索引 0..N-1 的共享映射。
///
/// 数据来自 <see cref="KeyMapConfig"/>：默认 4×4 QWERTY 布局，
/// 也可以从 exe 同目录下的 <c>config.json</c> 加载（由调用方在启动早期
/// <see cref="Configure"/>）。
///
/// 视觉布局和物理键位一一对应，肌肉记忆成本最低。
///
/// 用于：
///   1) SelectorWindow 渲染槽位标签 + 处理 WPF KeyDown
///   2) App 的全局低层键盘钩子判断哪些 VK 该拦截
/// </summary>
public static class KeyMap
{
    private static KeyMapConfig _config = KeyMapConfig.Default;
    private static Dictionary<int, int>? _vkToIndex;

    /// <summary>当前生效的配置。启动后调用一次 <see cref="Configure"/> 替换。</summary>
    public static KeyMapConfig Current => _config;

    /// <summary>键位总数。</summary>
    public static int Size => _config.Size;

    /// <summary>视觉网格的行列数。</summary>
    public static int Rows => _config.Rows;
    public static int Cols => _config.Cols;

    /// <summary>本地索引 → Win32 VirtualKey 码。</summary>
    public static int[] IndexToVk => _config.VirtualKeys;

    /// <summary>本地索引 → 显示用的字符串标签。</summary>
    public static string[] IndexToLabel => _config.Labels;

    /// <summary>
    /// 切换底层配置（一般只在程序启动早期调一次）。所有通过 <see cref="KeyMap"/>
    /// 静态属性读到的地方会立即看到新的配置。
    /// </summary>
    public static void Configure(KeyMapConfig config)
    {
        _config = config ?? KeyMapConfig.Default;
        _vkToIndex = null;
    }

    /// <summary>把 Win32 VirtualKey 转换为本地索引；不命中返回 null。</summary>
    public static int? ToIndex(int vk)
    {
        if (_vkToIndex == null) BuildCache();
        return _vkToIndex!.TryGetValue(vk, out int i) ? i : null;
    }

    /// <summary>索引 → 标签，越界返回 "?"。</summary>
    public static string LabelOf(int index) => _config.LabelOf(index);

    private static void BuildCache()
    {
        var map = new Dictionary<int, int>(_config.VirtualKeys.Length);
        for (int i = 0; i < _config.VirtualKeys.Length; i++)
        {
            int vk = _config.VirtualKeys[i];
            if (vk != 0 && !map.ContainsKey(vk)) map[vk] = i;
        }
        _vkToIndex = map;
    }
}