using System.ComponentModel;
using System.Windows;

namespace WinKit.Desktop.Shell;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || DataContext is not ShellViewModel shell || !shell.HasActiveWork) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        Title = "WinKit · 正在取消任务，请稍候";
        try { await shell.CancelAndWaitAsync(); }
        finally { _allowClose = true; Close(); }
    }
}
