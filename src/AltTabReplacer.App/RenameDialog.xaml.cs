using System.Windows;
using System.Windows.Input;

namespace AltTabReplacer;

/// <summary>
/// 极简的单行输入对话框，用于重命名槽位 / 组。
/// 不用 Microsoft.VisualBasic 的 InputBox，避免为一个小功能引入额外依赖。
/// </summary>
public partial class RenameDialog : Window
{
    private RenameDialog(string title, string initial)
    {
        InitializeComponent();
        PART_Label.Text = title;
        PART_Input.Text = initial;

        Loaded += (_, __) =>
        {
            PART_Input.Focus();
            PART_Input.SelectAll();     // 全选，方便直接覆盖输入
        };
    }

    /// <summary>弹出对话框。返回 null 表示取消，否则返回输入内容（可能是空串 = 恢复默认名）。</summary>
    public static string? Show(Window owner, string title, string initial)
    {
        var dlg = new RenameDialog(title, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.PART_Input.Text : null;
    }

    private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; DialogResult = true; }
        else if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
