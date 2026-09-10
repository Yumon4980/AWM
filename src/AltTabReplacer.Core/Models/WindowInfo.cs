using System;

namespace AltTabReplacer.Core.Models;

/// <summary>
/// 单个顶层窗口的快照。不可变。
/// </summary>
public sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string ProcessName,
    int ProcessId,
    bool IsMinimized
);
