using System.Windows.Controls;
using WinKit.Desktop.Infrastructure;
using WinKit.Desktop.Tools.NulCleanup;

namespace WinKit.Desktop.Shell;

// The registry describes real tools only. Each module owns its view and workflow.
public sealed record ToolDescriptor(string Id, string Title, string Category, string Description, string IconGeometry, ObservableObject ViewModel, Func<UserControl> CreateView);

public sealed class ToolRegistry
{
    public IReadOnlyList<ToolDescriptor> Tools { get; }
    public ToolRegistry(NulCleanupViewModel nulCleanup)
    {
        Tools = [new ToolDescriptor("nul-cleanup", "NUL 文件清理", "文件工具", "安全查找并清理 Windows 保留名称文件。",
            "M 5,2 L 15,2 L 20,7 L 20,22 L 5,22 Z M 15,2 L 15,7 L 20,7 M 9,12 L 16,12 M 9,16 L 16,16", nulCleanup,
            () => new NulCleanupView { DataContext = nulCleanup })];
    }
}
