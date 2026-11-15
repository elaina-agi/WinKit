using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinKit.Core.NulCleanup;

namespace WinKit.Desktop.Services;

public interface IDesktopDialogs
{
    string? BrowseFolder(string currentPath);
    bool ConfirmPermanentDelete(int count, string rootPath);
    void ShowMessage(string title, string message);
    void ShowScanIssues(IReadOnlyList<ScanIssue> issues, int skippedReparsePoints, bool cancelled);
    bool CopyText(string text);
    void OpenDirectory(string directory);
}

public sealed class DesktopDialogs : IDesktopDialogs
{
    private static Window? Owner => Application.Current?.MainWindow;

    public string? BrowseFolder(string currentPath)
    {
        var dialog = new OpenFolderDialog { Title = "选择扫描文件夹", Multiselect = false };
        if (Directory.Exists(currentPath)) dialog.InitialDirectory = currentPath;
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public bool ConfirmPermanentDelete(int count, string rootPath) => MessageBox.Show(Owner!,
        $"即将永久删除已勾选的 {count} 个 NUL 文件。\n\n扫描范围：{rootPath}\n\n不经过回收站，无法通过本工具撤销。删除前会再次核对文件身份和路径安全。\n\n确定继续吗？",
        "确认永久删除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowMessage(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowScanIssues(IReadOnlyList<ScanIssue> issues, int skippedReparsePoints, bool cancelled)
    {
        var text = $"扫描状态：{(cancelled ? "已取消（结果不完整）" : issues.Count > 0 ? "不完整（部分路径未能检查）" : "完成")}\n" +
            $"主动跳过的重解析点：{skippedReparsePoints} 个（链接、目录联接等不跟随）。\n\n" +
            (issues.Count == 0 ? "没有报告扫描错误。" : string.Join("\n\n", issues.Select(issue => $"路径：{issue.Path}\n原因：{issue.Message}")));
        var content = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(20), BorderThickness = new Thickness(0), Padding = new Thickness(8)
        };
        var window = new Window
        {
            Title = "扫描问题与跳过项", Owner = Owner, Width = 780, Height = 480, MinWidth = 460, MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("CanvasBrush"),
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont"), FontSize = 13
        };
        window.ShowDialog();
    }

    public bool CopyText(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch (Exception exception) { ShowMessage("复制失败", exception.Message); return false; }
    }

    public void OpenDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception) { ShowMessage("无法打开文件夹", exception.Message); }
    }
}
