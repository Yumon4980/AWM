using System.Windows;

namespace AltTabReplacer;

/// <summary>
/// 不可见窗口。仅作为 HotkeyService 接收 WM_HOTKEY 的目标 HWND。
/// </summary>
public partial class HostWindow : Window
{
    public HostWindow()
    {
        InitializeComponent();
    }
}
