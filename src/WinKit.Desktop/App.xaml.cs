using System.IO;
using System.Windows;
using WinKit.Core.NulCleanup;
using WinKit.Desktop.Pages;
using WinKit.Desktop.Services;
using WinKit.Desktop.Shell;
using WinKit.Desktop.Testing;
using WinKit.Desktop.Tools.NulCleanup;

namespace WinKit.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Length > 0 && e.Args[0] == "--ui-smoke";
        string? smokeOutput = null;
        try
        {
            string dataDirectory;
            if (smoke)
            {
                if (e.Args.Length != 3) throw new ArgumentException("用法：WinKit.exe --ui-smoke <测试文件夹> <输出文件夹>。只扫描，不执行删除。");
                smokeOutput = Path.GetFullPath(e.Args[2]);
                dataDirectory = Path.Combine(smokeOutput, "isolated-app-data");
            }
            else if (e.Args.Length > 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--test-data-directory")
                    throw new ArgumentException("支持参数：--test-data-directory <隔离数据目录>，或 --ui-smoke <测试文件夹> <输出文件夹>。");
                // Override persistence only: no automatic scans and no bypass of confirmation.
                dataDirectory = Path.GetFullPath(e.Args[1]);
            }
            else dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinKit");

            var settings = new SettingsStore(dataDirectory);
            var journal = new OperationJournal(dataDirectory);
            IDesktopDialogs dialogs = smoke ? new ScanOnlyTestDialogs() : new DesktopDialogs();
            var cleanup = new NulCleanupViewModel(new NulCleanupService(), dialogs, settings, journal);
            var registry = new ToolRegistry(cleanup);
            var shell = new ShellViewModel(registry, cleanup, new ActivityViewModel(journal, dialogs), new SettingsViewModel(settings, dialogs, journal));
            var window = new MainWindow { DataContext = shell };
            MainWindow = window;
            window.Show();
            await journal.LoadAsync();
            if (smoke)
            {
                var exitCode = await UiSmokeRunner.RunAsync(window, shell, e.Args[1], smokeOutput!);
                Shutdown(exitCode);
            }
        }
        catch (Exception exception)
        {
            if (smoke)
            {
                try
                {
                    if (smokeOutput is not null)
                    {
                        Directory.CreateDirectory(smokeOutput);
                        await File.WriteAllTextAsync(Path.Combine(smokeOutput, "smoke-error.txt"), exception.ToString());
                    }
                }
                catch { /* Report failure via process exit even when output cannot be saved. */ }
            }
            else MessageBox.Show($"WinKit 启动失败：\n{exception.Message}", "WinKit", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
