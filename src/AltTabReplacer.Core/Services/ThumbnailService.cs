using System;
using System.Collections.Generic;
using AltTabReplacer.Core.Infrastructure;
using static AltTabReplacer.Core.Infrastructure.Win32.NativeMethods;
using static AltTabReplacer.Core.Infrastructure.Win32.Structures;

namespace AltTabReplacer.Core.Services;

/// <summary>
/// DWM 缩略图服务：为目标 HWND 创建缩略图到 destHwnd 上的指定矩形。
/// 必须 UI 线程调用。
/// </summary>
public sealed class ThumbnailService
{
    private readonly Dictionary<IntPtr, IntPtr> _registered = new();   // hThumbnail -> sourceHwnd

    /// <summary>注册缩略图并返回 hThumbnail。失败返回 IntPtr.Zero。</summary>
    public IntPtr Register(IntPtr destHwnd, IntPtr sourceHwnd)
    {
        if (destHwnd == IntPtr.Zero || sourceHwnd == IntPtr.Zero) return IntPtr.Zero;
        if (_registered.ContainsKey(sourceHwnd))
        {
            return _registered[sourceHwnd];
        }

        int hr = DwmRegisterThumbnail(destHwnd, sourceHwnd, out IntPtr hThumb);
        if (hr != 0 || hThumb == IntPtr.Zero)
        {
            Logger.Warn($"DwmRegisterThumbnail(src=0x{sourceHwnd:X}) 失败，hr=0x{hr:X}");
            return IntPtr.Zero;
        }
        _registered[hThumb] = sourceHwnd;
        return hThumb;
    }

    /// <summary>更新缩略图位置与可见性。dest 是以 destHwnd 客户区左上角为原点的矩形。</summary>
    public bool Update(IntPtr hThumb, RECT dest, bool visible = true)
    {
        if (hThumb == IntPtr.Zero) return false;

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = (uint)(DWM_TNP.Destination | DWM_TNP.Visible | DWM_TNP.SourceClientAreaOnly | DWM_TNP.Opacity),
            Destination = dest,
            Source = default,
            Opacity = 255,
            Visible = visible,
            SourceClientAreaOnly = false,
        };

        int hr = DwmUpdateThumbnailProperties(hThumb, ref props);
        return hr == 0;
    }

    public void Unregister(IntPtr hThumb)
    {
        if (hThumb == IntPtr.Zero) return;
        DwmUnregisterThumbnail(hThumb);
        _registered.Remove(hThumb);
    }

    public void UnregisterAll()
    {
        foreach (var hThumb in new List<IntPtr>(_registered.Keys))
        {
            DwmUnregisterThumbnail(hThumb);
        }
        _registered.Clear();
    }
}
