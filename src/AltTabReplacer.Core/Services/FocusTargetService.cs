using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Threading;
using AltTabReplacer.Core.Infrastructure;
using AltTabReplacer.Core.Models;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// "切换程序后聚焦到指定输入框"的 UIA 实现。两个职责：
///
///   捕获 —— 捕获遮罩把屏幕上某一点的 UIA 元素读出来，抽成 <see cref="FocusTargetEntry"/>；
///   应用 —— 窗口激活后在它的控件树里按 AutomationId → Name → ControlType 找回输入框并 SetFocus。
///
/// 全部走 UIA（System.Windows.Automation），不碰 AttachThreadInput：
/// SetFocus 通过 provider 跨进程生效，对 WPF / WinForms / Electron / 浏览器网页内输入框都有效。
///
/// **线程模型（实测踩坑后定下来的）**：托管的 UIAutomationClient 包装器有严重的线程亲和问题，
/// 换着花样在后台线程上调用，要么永远挂死、要么慢 60 倍（详见 git 历史）；唯独 UI 线程上
/// 稳定且够快（FromHandle 5-32ms、小树 FindFirst 20-56ms）。所以**所有 UIA 调用只在 UI 线程**：
/// 捕获同步执行；应用通过 <see cref="Dispatcher.BeginInvoke"/> 排队——等选择器关完、
/// 消息泵空闲时再跑，切换路径零阻塞。
///
/// 局限：本进程非提权时看不见提权窗口的控件树（UIPI）；游戏等自绘界面没有控件树，捕获不到。
/// </summary>
public sealed class FocusTargetService
{
    private readonly FocusTargetStore _store;
    private readonly Func<bool> _isEnabled;
    private readonly Dispatcher _uiDispatcher;
    private readonly Func<IntPtr, bool> _isForeground;

    public FocusTargetService(FocusTargetStore store, Func<bool> isEnabled, Dispatcher uiDispatcher)
        : this(store, isEnabled, uiDispatcher, foregroundCheck: null)
    {
    }

    /// <summary>foregroundCheck 仅供测试注入（真实场景恒为"目标窗口是否前台"）。</summary>
    public FocusTargetService(FocusTargetStore store, Func<bool> isEnabled, Dispatcher uiDispatcher,
        Func<IntPtr, bool>? foregroundCheck)
    {
        _store = store;
        _isEnabled = isEnabled;
        _uiDispatcher = uiDispatcher;
        _isForeground = foregroundCheck ?? (hwnd => GetForegroundWindow() == hwnd);
    }

    /// <summary>本进程名（去掉 .exe），捕获遮罩用它拒绝"点到自己"。</summary>
    public static string OwnProcessName => NormalizeProcess(
        Process.GetCurrentProcess().ProcessName);

    // ---- 配置存取（UI 只面对 service，不直接碰 store）----

    public IReadOnlyList<FocusTargetEntry> Entries => _store.Snapshot();

    public void Save(FocusTargetEntry entry) => _store.Upsert(entry);

    public bool Remove(string processName) => _store.Remove(processName);

    /// <summary>进程名统一成不带 .exe 的形式，作为配置键。</summary>
    public static string NormalizeProcess(string processName) =>
        processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

    /// <summary>
    /// 桌面根窗口（csrss 的 #32769）：UIA 看不见点击点下的真窗口时 FromPoint 的兜底结果，
    /// 典型原因是目标以管理员运行（UIPI 权限屏障）。这种数据无效，调用方必须拒绝入库。
    /// </summary>
    public static bool IsDesktopRoot(FocusTargetEntry e) =>
        string.Equals(e.ClassName, "#32769", StringComparison.Ordinal)
        || string.Equals(e.Process, "csrss", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    //  捕获：屏幕坐标 → 配置条目（UI 线程同步执行）
    // ============================================================

    /// <summary>
    /// 读取屏幕上一点（物理像素坐标）的 UIA 元素，抽成配置条目。
    /// 返回 null 表示该点没有可识别的元素。抛异常（提权窗口等）由调用方接住。
    ///
    /// 叶子元素没有 AutomationId 也没有 Name 时向上爬（最多 5 层）找第一个"可识别"的祖先：
    /// 自绘控件的叶子常常是匿名的，身份在其容器上。**例外**：叶子本身是输入类控件
    /// （Edit / Document / ComboBox）时绝不爬——经典 Win32 的 EDIT 经 MSAA 桥接后就是
    /// 匿名的，爬上去会录成整个窗口，聚焦就落错了地方。这种叶子的身份靠
    /// ControlType + ClassName 表达，照样能在应用时精确找回。
    /// </summary>
    public FocusTargetEntry? CaptureAt(double screenX, double screenY)
    {
        return CaptureCore(screenX, screenY);
    }

    private FocusTargetEntry? CaptureCore(double screenX, double screenY)
    {
        var el = AutomationElement.FromPoint(new System.Windows.Point(screenX, screenY));
        if (el == null) return null;

        int pid = el.Current.ProcessId;
        string proc = ProcessNameOf(pid);
        if (string.IsNullOrEmpty(proc)) return null;

        var cur = el;
        for (int depth = 0; depth < 5; depth++)
        {
            if (!string.IsNullOrEmpty(cur.Current.AutomationId)
                || !string.IsNullOrEmpty(cur.Current.Name)) break;
            if (IsInputControl(cur)) break;
            var parent = TreeWalker.ControlViewWalker.GetParent(cur);
            if (parent == null || parent == AutomationElement.RootElement) break;
            cur = parent;
        }

        var entry = new FocusTargetEntry
        {
            Process = NormalizeProcess(proc),
            AutomationId = NullIfEmpty(cur.Current.AutomationId),
            Name = NullIfEmpty(cur.Current.Name),
            ControlTypeId = cur.Current.ControlType?.Id ?? 0,
            ClassName = NormalizeClassName(cur.Current.ClassName),
            CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        if (entry.AutomationId == null && entry.Name == null
            && entry.ControlTypeId == 0 && entry.ClassName == null)
            return null;
        Logger.Info($"捕获输入框: {entry.Process} id={entry.AutomationId ?? "-"} name={Truncate(entry.Name)} ctl={entry.ControlTypeId} class={entry.ClassName ?? "-"}");
        return entry;
    }

    /// <summary>输入类控件：内容即身份的叶子，匿名也不能被祖先顶替。</summary>
    private static bool IsInputControl(AutomationElement el)
    {
        int id = el.Current.ControlType?.Id ?? 0;
        return id == ControlType.Edit.Id
            || id == ControlType.Document.Id
            || id == ControlType.ComboBox.Id;
    }

    private static string ProcessNameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return NormalizeProcess(p.ProcessName);
        }
        catch (Exception)
        {
            // 进程恰好退出了（窗口关闭竞态）：这条捕获作废
            return "";
        }
    }

    /// <summary>
    /// WinForms 的窗口类名带**每次进程随机生成**的哈希后缀
    /// （如 "WindowsForms10.Edit.app.0.1f550a4_r3_ad1"，重启后缀就变），
    /// 原样存储的话跨重启必然失配。在 ".app." 处截断，保留稳定前缀。
    /// </summary>
    public static string? NormalizeClassName(string? className)
    {
        if (string.IsNullOrEmpty(className)) return null;
        if (className.StartsWith("WindowsForms10.", StringComparison.Ordinal))
        {
            int cut = className.IndexOf(".app.", StringComparison.Ordinal);
            if (cut > 0) return className[..cut];
        }
        return className;
    }

    // ============================================================
    //  应用：窗口激活后找回输入框并聚焦
    // ============================================================

    /// <summary>
    /// 激活 <paramref name="target"/> 后聚焦其录制的输入框。异步执行，不阻塞切换：
    /// UIA 树查找在慢程序（浏览器）上可能要几十到几百毫秒，不能摊在 UI 线程上。
    /// 找到前目标窗口若已不是前台（用户又切走了）就放弃，不去抢焦点。
    /// </summary>
    public void BeginApply(WindowInfo target)
    {
        if (target.Hwnd == IntPtr.Zero) return;
        if (!_isEnabled()) return;
        var entry = _store.Find(target.ProcessName);
        if (entry == null) return;

        // 排队到 UI 线程异步执行：等当前 UI 活儿（选择器关闭）做完、消息泵空闲时再跑。
        // 异常自己拦住——UI 线程派发的未处理异常会把整个程序带走。
        _uiDispatcher.BeginInvoke(() =>
        {
            try { ApplyCore(target.Hwnd, entry); }
            catch (Exception ex)
            {
                Logger.Warn($"聚焦输入框失败 ({entry.Process}): {ex.Message}");
            }
        });
    }

    private void ApplyCore(IntPtr hwnd, FocusTargetEntry entry)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var (el, clue) = FindMatch(root, entry);
        if (el == null)
        {
            // 窗口刚从最小化恢复时可能还没布局完，等一拍再试一次
            Thread.Sleep(150);
            root = AutomationElement.FromHandle(hwnd);
            (el, clue) = FindMatch(root, entry);
        }
        if (el == null)
        {
            Logger.Info($"未找到输入框 ({entry.Process})，线索 id={entry.AutomationId ?? "-"} name={Truncate(entry.Name)}");
            return;
        }

        // 捕获/应用之间隔了用户操作，确认目标仍是前台再动手，别把焦点抢到后台窗口
        if (!_isForeground(hwnd))
        {
            Logger.Info($"跳过聚焦 ({entry.Process})：窗口已不是前台");
            return;
        }
        el.SetFocus();
        Logger.Info($"已聚焦输入框 ({entry.Process}，按{clue})");
    }

    /// <summary>
    /// 按稳定性依次尝试 AutomationId → Name → (ControlType+ClassName) → ControlType。
    /// 最后一层裸 ControlType 兜底是给类名漂移准备的（旧配置里的全量 WinForms 类名、
    /// 类名被 NormalizeClassName 漏掉的变体），命中面宽但聊胜于无。返回命中的线索名。
    /// </summary>
    private static (AutomationElement? el, string clue) FindMatch(AutomationElement? root, FocusTargetEntry e)
    {
        if (root == null) return (null, "");

        if (!string.IsNullOrEmpty(e.AutomationId))
        {
            var el = root.FindFirst(TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, e.AutomationId));
            if (el != null) return (el, "AutomationId");
        }

        if (!string.IsNullOrEmpty(e.Name))
        {
            var el = root.FindFirst(TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, e.Name));
            if (el != null) return (el, "Name");
        }

        if (e.ControlTypeId == 0) return (null, "");
        try
        {
            var ct = ControlType.LookupById(e.ControlTypeId);
            if (ct == null) return (null, "");

            if (!string.IsNullOrEmpty(e.ClassName))
            {
                var el = root.FindFirst(TreeScope.Element | TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ct),
                        new PropertyCondition(AutomationElement.ClassNameProperty, e.ClassName)));
                if (el != null) return (el, "ControlType+Class");
            }

            var any = root.FindFirst(TreeScope.Element | TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
            if (any != null) return (any, "ControlType");
        }
        catch (ArgumentException)
        {
            // 控件类型 Id 越界（理论上不会），跳过这条兜底
        }
        return (null, "");
    }

    // ============================================================
    //  展示辅助（设置界面用）
    // ============================================================

    /// <summary>控件类型 Id → 短名（"Edit" 等）。未知返回 null。</summary>
    public static string? ControlTypeName(int id)
    {
        if (id == 0) return null;
        try
        {
            var name = ControlType.LookupById(id)?.ProgrammaticName;
            return name?.Replace("ControlType.", "");
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>条目的单行描述，列表里展示用。</summary>
    public static string Describe(FocusTargetEntry e)
    {
        string ctl = ControlTypeName(e.ControlTypeId) ?? "元素";
        string? label = !string.IsNullOrEmpty(e.AutomationId) ? e.AutomationId
            : !string.IsNullOrEmpty(e.Name) ? Truncate(e.Name)
            : !string.IsNullOrEmpty(e.ClassName) ? e.ClassName
            : null;
        return label == null ? ctl : $"{ctl} · {label}";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length <= 24 ? s : s[..24] + "…";
    }
}
