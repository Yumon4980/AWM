using System;
using System.Collections.Generic;
using System.Text;
using AltTabReplacer.Core.Infrastructure;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// 由进程 ID 反查可执行文件路径。程序组合靠它记住"未开时该启动什么"。
///
/// 结果按 PID 缓存：枚举/解析期间同一个进程会被反复问，而 OpenProcess 不便宜。
/// </summary>
public sealed class ProcessPathResolver
{
    private readonly Dictionary<int, string?> _cache = new();

    /// <summary>查进程的可执行文件全路径；查不到返回 null。</summary>
    public string? GetPath(int processId)
    {
        if (processId <= 0) return null;
        if (_cache.TryGetValue(processId, out var cached)) return cached;

        string? path = null;
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (h != IntPtr.Zero)
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size))
                {
                    path = sb.ToString(0, size);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"查询进程路径失败 (pid={processId}): {ex.Message}");
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }

        _cache[processId] = path;
        return path;
    }
}
