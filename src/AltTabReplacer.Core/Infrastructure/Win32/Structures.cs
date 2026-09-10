using System.Runtime.InteropServices;

namespace AltTabReplacer.Core.Infrastructure.Win32;

/// <summary>
/// 通用 Win32 结构体集中管理。
/// </summary>
public static class Structures
{
    /// <summary>RECT 矩形。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>WINDOWPLACEMENT：用于查询最小化状态。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// DWM 缩略图属性。flags 字段选择启用哪些字段。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint Flags;
        public RECT Destination;
        public RECT Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    /// <summary>
    /// DWM_THUMBNAIL_PROPERTIES 的 flags 位掩码。
    /// </summary>
    [Flags]
    public enum DWM_TNP : uint
    {
        Destination = 0x0001,
        Source = 0x0002,
        Opacity = 0x0004,
        Visible = 0x0008,
        SourceClientAreaOnly = 0x0010,
    }
}
