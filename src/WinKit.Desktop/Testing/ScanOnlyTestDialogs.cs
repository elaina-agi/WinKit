using WinKit.Core.NulCleanup;
using WinKit.Desktop.Services;

namespace WinKit.Desktop.Testing;

// Smoke mode renders ordinary enabled controls but can never grant delete permission.
internal sealed class ScanOnlyTestDialogs : IDesktopDialogs
{
    public string? BrowseFolder(string currentPath) => null;
    public bool ConfirmPermanentDelete(int count, string rootPath) => false;
    public void ShowMessage(string title, string message) { }
    public void ShowScanIssues(IReadOnlyList<ScanIssue> issues, int skippedReparsePoints, bool cancelled) { }
    public bool CopyText(string text) => false;
    public void OpenDirectory(string directory) { }
}
