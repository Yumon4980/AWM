using System;
using System.Runtime.InteropServices;
using AltTabReplacer.Core.Infrastructure;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 通过 Shell.Application COM（IShellWindows）读取 explorer 窗口正在浏览的真实路径。
///
/// 为什么不用窗口标题：explorer 会对长路径自动截断（中间插 "..."，如 <c>D:\Pro...\sub</c>），
/// 截断后的标题无法还原成可用的路径。COM 的 <c>LocationURL</c> / <c>Document.Folder.Self.Path</c>
/// 返回的永远是完整路径，是唯一可靠的来源。
///
/// 适用范围：ShellWindows 只暴露 explorer 文件夹窗口（含 Win11 的多标签，每个标签一条记录），
/// 恰好就是我们要的对象；IE 已被系统移除，不会产生干扰项。
/// </summary>
public static class ExplorerPathResolver
{
    /// <summary>取 explorer 窗口（hwnd）当前浏览的文件系统路径；取不到返回 null。</summary>
    public static string? GetPath(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                Logger.Warn("Shell.Application COM 不可用，无法读取 explorer 窗口路径");
                return null;
            }
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic windows = shell.Windows();
                try
                {
                    foreach (dynamic w in windows)
                    {
                        try
                        {
                            if ((long)w.HWND != (long)hwnd) continue;
                            // LocationURL 对普通文件夹返回 file:/// URL；特殊位置为空时
                            // 退回 Document.Folder.Self.Path（原始 PIDL 字符串形式）
                            string? path = TryLocalPath(w.LocationURL as string)
                                ?? TryLocalPath(TryDocumentPath(w));
                            if (path != null) return path;
                        }
                        finally
                        {
                            Release(w);
                        }
                    }
                }
                finally
                {
                    Release(windows);
                }
            }
            finally
            {
                Release(shell);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 explorer 窗口路径失败 (hwnd=0x{hwnd:X}): {ex.Message}");
        }
        return null;
    }

    /// <summary>从 Document.Folder.Self.Path 取路径；中间任何一环为空都返回 null。</summary>
    private static string? TryDocumentPath(dynamic w)
    {
        try
        {
            var doc = w.Document;
            if (doc == null) return null;
            var folder = doc.Folder;
            if (folder == null) return null;
            var self = folder.Self;
            return self?.Path as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// file:/// URL / 盘符路径 / UNC → 文件系统路径。
    /// 虚拟位置（"::{CLSID}"、"此电脑"、"回收站"）返回 null——没法用命令行复现。
    /// </summary>
    private static string? TryLocalPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                return new Uri(raw).LocalPath;
            if (raw.StartsWith("::", StringComparison.Ordinal)) return null;
            if (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':') return raw;
            if (raw.Length >= 2 && raw[0] == '\\' && raw[1] == '\\') return raw;
            return null;
        }
        catch { return null; }
    }

    private static void Release(object? com)
    {
        try
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }
        catch { /* 释放失败可以忽略，GC 兜底 */ }
    }
}
