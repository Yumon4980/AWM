using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AltTabReplacer.Core.Infrastructure;

namespace AltTabReplacer.Core;

/// <summary>
/// 选择器键位网格的可配置映射：从 exe 同目录下的 <c>config.json</c> 读取。
///
/// JSON 格式是一个二维数组，**外层每个元素是一行（对应网格的"行"），
/// 内层每个元素是该行的一个键位标签**：
///
/// <code>
/// [
///   ["1", "2", "3", "4", "5"],
///   ["q", "w", "e", "r", "t"],
///   ["a", "s", "d", "f", "g"],
///   ["z", "x", "c", "v", "b"]
/// ]
/// </code>
///
/// 单元值支持的写法：
///   - 数字（0~9）         → 0x30 ~ 0x39
///   - 大小写字母 A~Z / a~z → 0x41 ~ 0x5A
///   - 特殊键："space" "tab" "enter" "esc" "backspace" "↑/up" ↓/down" "←/left" "→/right"
///   - 数字也可以写成字符串 "1"
///
/// 各行长度不要求相同（取最长一行的长度作为列数）。空字符串 / 0 → 该键位不响应。
///
/// 文件不存在 / 解析失败时退回 <see cref="Default"/>（与旧 <see cref="KeyMap"/>
/// 行为一致的 4×4 QWERTY 布局），不会让程序启动失败。
/// </summary>
public sealed class KeyMapConfig
{
    /// <summary>键位网格的行数。</summary>
    public int Rows { get; init; }

    /// <summary>键位网格的列数。</summary>
    public int Cols { get; init; }

    /// <summary>键位总数 = Rows * Cols。</summary>
    public int Size => Rows * Cols;

    /// <summary>按行优先排列的显示标签（长度 = Size）。</summary>
    public string[] Labels { get; init; } = Array.Empty<string>();

    /// <summary>按行优先排列的 Win32 VirtualKey 码（长度 = Size）。值 0 = 槽位存在但不响应按键。</summary>
    public int[] VirtualKeys { get; init; } = Array.Empty<int>();

    /// <summary>从索引取标签，越界返回 "?"。</summary>
    public string LabelOf(int i) =>
        i >= 0 && i < Labels.Length ? Labels[i] : "?";

    /// <summary>从索引取 VK，越界返回 0。</summary>
    public int VkOf(int i) =>
        i >= 0 && i < VirtualKeys.Length ? VirtualKeys[i] : 0;

    // ============================================================
    //  默认：4×4 QWERTY 布局，与改造前完全一致
    // ============================================================

    public static KeyMapConfig Default => new()
    {
        Rows = 4, Cols = 4,
        Labels = new[]
        {
            "1", "2", "3", "4",
            "Q", "W", "E", "R",
            "A", "S", "D", "F",
            "Z", "X", "C", "V",
        },
        VirtualKeys = new[]
        {
            0x31, 0x32, 0x33, 0x34,         // 1 2 3 4
            0x51, 0x57, 0x45, 0x52,         // Q W E R
            0x41, 0x53, 0x44, 0x46,         // A S D F
            0x5A, 0x58, 0x43, 0x56,         // Z X C V
        },
    };

    // ============================================================
    //  加载
    // ============================================================

    /// <summary>
    /// 默认配置文件路径查找顺序：
    ///   1) exe 同目录 / config.json（发布版 / 手动放置）；
    ///   2) 向上找 1～4 级目录里的 config.json（开发模式：
    ///    exe 在 `src\...\App\bin\Debug\...`，项目根的 config.json 在 4 层之上）。
    /// <br/>
    /// 找不到也不报错—— <see cref="Load"/> 拿到一个不存在的路径会退回默认布局。
    /// 返回值仅决定“默认去哪找”，会被 Load 重检查存在性。
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            try
            {
                string? exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                string exeDir = !string.IsNullOrEmpty(exe)
                    ? (Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory)
                    : AppContext.BaseDirectory;

                // 优先：exe 同目录
                string candidate = Path.Combine(exeDir, "config.json");
                if (File.Exists(candidate)) return candidate;

                // Fallback：向上找项目根（常见路径 src/App/bin/Config/... → 项目根）
                string? dir = exeDir;
                for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
                {
                    dir = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(dir)) break;
                    string c = Path.Combine(dir, "config.json");
                    if (File.Exists(c)) return c;
                }

                return candidate;   // 不存在也返回原路径，Load 里走 fallback
            }
            catch
            {
                return Path.Combine(AppContext.BaseDirectory, "config.json");
            }
        }
    }

    /// <summary>加载 <paramref name="path"/> 指定的 JSON；不存在 / 解析失败退回 <see cref="Default"/>。</summary>
    public static KeyMapConfig Load(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Logger.Info($"键位配置文件不存在，使用默认 4×4 QWERTY 布局: {path ?? "(null)"}");
            return Default;
        }

        try
        {
            string json = File.ReadAllText(path);
            // 先试标准 JSON（数字和字母都必须带引号）。失败则退回宽松解析：
            // 允许数字、字母裸写（不需引号），与 JSON5 习惯一致——配置场景下比要求
            // 每个单元都加引号更顺手。
            List<string[]> rows;
            try { rows = ParseStrict(json); }
            catch (JsonException) { rows = ParseLenient(json); }

            var cfg = Build(rows);
            Logger.Info($"加载键位配置: {cfg.Rows}×{cfg.Cols}（{cfg.Size} 个键位） ← {path}");
            return cfg;
        }
        catch (Exception ex)
        {
            Logger.Warn($"config.json 解析失败，使用默认布局: {ex.Message}");
            return Default;
        }
    }

    /// <summary>用 <see cref="JsonDocument"/> 严格解析：每个单元都必须是标准 JSON 值（数字 / 字符串）。</summary>
    private static List<string[]> ParseStrict(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("根元素必须是数组");

        var rows = new List<string[]>();
        foreach (var rowEl in doc.RootElement.EnumerateArray())
        {
            if (rowEl.ValueKind != JsonValueKind.Array)
                throw new JsonException("每一行必须是数组");
            var row = new List<string>(rowEl.GetArrayLength());
            foreach (var cell in rowEl.EnumerateArray())
                row.Add(ParseCell(cell));
            rows.Add(row.ToArray());
        }
        return rows;
    }

    /// <summary>
    /// 宽松解析（JSON5 风格）：仅支持二维数组，单元允许裸数字 / 裸标识符 / 字符串。
    /// 专为配置场景写的小型解析器，足够覆盖常见写法且不会误伤合法 JSON。
    /// </summary>
    private static List<string[]> ParseLenient(string json)
    {
        var rows = new List<string[]>();
        int i = 0;
        Skip(json, ref i);
        ExpectChar(json, ref i, '[');
        while (true)
        {
            Skip(json, ref i);
            if (i < json.Length && json[i] == ']') { i++; break; }
            var row = ParseRow(json, ref i);
            rows.Add(row);
            Skip(json, ref i);
            if (i < json.Length && json[i] == ',') { i++; continue; }
            if (i < json.Length && json[i] == ']') { i++; break; }
            throw new FormatException($"config.json 解析到位置 {i} 预期 ',' 或 ']'，却遇到 '{Peek(json, i)}'");
        }
        return rows;
    }

    private static string[] ParseRow(string s, ref int i)
    {
        Skip(s, ref i);
        ExpectChar(s, ref i, '[');
        var row = new List<string>();
        while (true)
        {
            Skip(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return row.ToArray(); }
            row.Add(ParseAtom(s, ref i));
            Skip(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return row.ToArray(); }
            throw new FormatException($"config.json 行内位置 {i} 预期 ',' 或 ']'");
        }
    }

    /// <summary>解析一个原子值：字符串 / 裸数字 / 裸字母标识符。</summary>
    private static string ParseAtom(string s, ref int i)
    {
        Skip(s, ref i);
        if (i >= s.Length) throw new FormatException("意外的文件结束");
        if (s[i] == '"') return ReadString(s, ref i);

        int start = i;
        // 数字（含负号 / 小数点）
        if (s[i] == '-' || (s[i] >= '0' && s[i] <= '9'))
        {
            i++;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.' || s[i] == '-')) i++;
            return s[start..i];
        }
        // 字母 / 下划线开头的裸标识符（不能跨越逗号 / 括号）
        if (char.IsLetter(s[i]) || s[i] == '_')
        {
            i++;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
            return s[start..i];
        }
        // 中文等其它 Unicode 字母
        if (char.IsLetter(s[i]))
        {
            i++;
            return s[start..i];
        }
        throw new FormatException($"config.json 位置 {i} 处无法识别的字符 '{s[i]}'");
    }

    private static string ReadString(string s, ref int i)
    {
        if (s[i] != '"') throw new FormatException("字符串应以双引号开始");
        i++;
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            if (i >= s.Length) throw new FormatException("字符串未提前闭合");
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c == '\\' && i < s.Length)
            {
                char esc = s[i++];
                sb.Append(esc switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"',
                    '\\' => '\\', '/' => '/', _ => esc,
                });
                continue;
            }
            sb.Append(c);
        }
    }

    private static void Skip(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) i++;
            else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
            }
            else break;
        }
    }

    private static void ExpectChar(string s, ref int i, char c)
    {
        Skip(s, ref i);
        if (i >= s.Length || s[i] != c)
            throw new FormatException($"位置 {i} 预期 '{c}'");
        i++;
    }

    private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>把 JSON 单元转换为标签字符串：数字取原文本，字符串原样保留。</summary>
    private static string ParseCell(JsonElement cell)
    {
        return cell.ValueKind switch
        {
            JsonValueKind.Number => cell.GetRawText(),         // "1" / "3.5" 等
            JsonValueKind.String => cell.GetString() ?? "",
            _ => cell.GetRawText(),
        };
    }

    /// <summary>把二维标签数组展平成行优先的 KeyMapConfig。允许各行列数不同（取最大列数对齐）。</summary>
    private static KeyMapConfig Build(IReadOnlyList<string[]> rows)
    {
        int r = rows.Count;
        if (r == 0) return Default;
        int c = rows.Max(row => row.Length);
        if (c == 0) return Default;

        var labels = new string[r * c];
        var vks = new int[r * c];
        var seen = new Dictionary<int, int>();   // vk → 已占用索引

        for (int i = 0; i < r; i++)
        {
            for (int j = 0; j < c; j++)
            {
                int idx = i * c + j;
                string label = j < rows[i].Length ? (rows[i][j] ?? "") : "";
                int vk;

                if (string.IsNullOrEmpty(label))
                {
                    vk = 0;
                    label = " ";
                }
                else if (!TryParseToken(label, out vk))
                {
                    Logger.Warn($"无法识别的键位 '{label}'（行 {i + 1} 列 {j + 1}），该键位已置空");
                    vk = 0;
                    label = "?";
                }
                else if (vk != 0 && seen.TryGetValue(vk, out int prevIdx))
                {
                    // 重复键位：后一个改成空位，否则两个槽位会绑死在同一物理键上
                    int pr = prevIdx / c, pc = prevIdx % c;
                    Logger.Warn(
                        $"键位重复：'{label}' (VK=0x{vk:X}) 在第 {pr + 1} 行 {pc + 1} 列 与 第 {i + 1} 行 {j + 1} 列，" +
                        "后者已置空");
                    vk = 0;
                    label = "?";
                }

                labels[idx] = label;
                vks[idx] = vk;
                if (vk != 0) seen[vk] = idx;
            }
        }

        return new KeyMapConfig
        {
            Rows = r, Cols = c,
            Labels = labels, VirtualKeys = vks,
        };
    }

    /// <summary>
    /// 把单个标签字符串解析为 Win32 VK。支持的格式：
    ///   数字字符 '0'~'9'                     → 0x30..0x39
    ///   大写字母 'A'~'Z'                     → 0x41..0x5A
    ///   小写字母 'a'~'z'                     → 0x41..0x5A
    ///   "space"/" "、"tab"、"enter"            → 0x20 / 0x09 / 0x0D
    ///   "esc"/"escape"、"bsp"/"backspace"     → 0x1B / 0x08
    ///   "↑"/"up"、"↓"/"down"、"←"/"left"、"→"/"right"  → 0x26/0x28/0x25/0x27
    /// </summary>
    public static bool TryParseToken(string s, out int vk)
    {
        vk = 0;
        if (string.IsNullOrEmpty(s)) return false;
        s = s.Trim();

        // 数字（不要求长度 = 1，但只解析单字符数字 0..9）
        if (s.Length == 1 && s[0] >= '0' && s[0] <= '9')
        {
            vk = 0x30 + (s[0] - '0');
            return true;
        }
        if (s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z')
        {
            vk = 0x41 + (s[0] - 'A');
            return true;
        }
        if (s.Length == 1 && s[0] >= 'a' && s[0] <= 'z')
        {
            vk = 0x41 + (s[0] - 'a');
            return true;
        }

        switch (s.ToLowerInvariant())
        {
            case "space":
            case "spc":
            case " ":
                vk = 0x20; return true;
            case "tab": vk = 0x09; return true;
            case "enter":
            case "return":
            case "cr":
                vk = 0x0D; return true;
            case "esc":
            case "escape":
                vk = 0x1B; return true;
            case "bsp":
            case "backspace":
            case "back":
                vk = 0x08; return true;
            case "up":
            case "↑":
                vk = 0x26; return true;
            case "down":
            case "↓":
                vk = 0x28; return true;
            case "left":
            case "←":
                vk = 0x25; return true;
            case "right":
            case "→":
                vk = 0x27; return true;
        }
        return false;
    }
}