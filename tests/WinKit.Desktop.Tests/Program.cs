using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WinKit.Desktop.Tests;

internal static class Program
{
    private static string _fixture = "";
    private static Process? _app;
    private static AutomationElement? _window;
    private static int _assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: WinKit.Desktop.Tests <absolute WinKit.exe>");
            return 2;
        }

        _fixture = Path.Combine(Path.GetTempPath(), "WinKit-Desktop-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Path.Combine(_fixture, "待检查项目");
            var nested = Path.Combine(folder, "子目录");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Extended(Path.Combine(folder, "nul")));
            var first = Path.Combine(nested, "nul");
            var secondFolder = Path.Combine(folder, "another");
            Directory.CreateDirectory(secondFolder);
            var second = Path.Combine(secondFolder, "NUL");
            CreateNativeFile(first);
            if (!CreateHardLinkW(Extended(second), Extended(first), IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not create hard-link UI fixture.");
            File.WriteAllText(Path.Combine(folder, "null"), "keep null");
            File.WriteAllText(Extended(Path.Combine(folder, "nul.txt")), "keep nul.txt");
            File.WriteAllText(Path.Combine(folder, "normal.txt"), "keep normal");

            var start = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false };
            start.ArgumentList.Add("--test-data-directory");
            start.ArgumentList.Add(Path.Combine(_fixture, "app-data"));
            _app = Process.Start(start) ?? throw new Exception("Could not start app.");
            _window = WaitForWindow(_app.Id, "WinKit", TimeSpan.FromSeconds(40));
            Console.WriteLine("App window found.");

            SetText("FolderPath", Path.Combine(_fixture, "does-not-exist"));
            Invoke("Scan");
            WaitUntil(() => Control("Scan").Current.IsEnabled, "invalid-path scan completes without crashing");
            Assert(!Control("DeleteSelected").Current.IsEnabled, "Invalid path cannot enable deletion");
            Assert(GetToggles(Control("Results")).Length == 0, "Invalid path produces no actionable rows");
            SetText("FolderPath", folder);
            var browseInvoke = (InvokePattern)Control("BrowseFolder").GetCurrentPattern(InvokePattern.Pattern);
            var browseTask = Task.Run(() => browseInvoke.Invoke());
            var folderDialog = WaitForWindow(_app.Id, "选择扫描文件夹", TimeSpan.FromSeconds(15));
            Assert(folderDialog.Current.ControlType == ControlType.Window, "Native folder picker opens");
            ((WindowPattern)folderDialog.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Assert(browseTask.Wait(10000), "Folder picker cancel returns");
            Assert(((ValuePattern)Control("FolderPath").GetCurrentPattern(ValuePattern.Pattern)).Current.Value == folder, "Cancelling folder picker preserves typed path");
            Assert(!Control("DeleteSelected").Current.IsEnabled, "Delete initially disabled");
            Invoke("Scan");
            WaitUntil(() => Control("Scan").Current.IsEnabled, "scan completes");
            var grid = Control("Results");
            WaitUntil(() => GetToggles(grid).Length == 2, "exactly two NUL rows");
            Assert(GetToggles(grid).All(x => ((TogglePattern)x.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.Off), "Rows initially unchecked");
            Assert(!Control("DeleteSelected").Current.IsEnabled, "Scan results not auto-selected");

            Invoke("SelectAll");
            WaitUntil(() => Control("DeleteSelected").Current.IsEnabled, "select all enables deletion");
            Assert(GetToggles(grid).All(x => ((TogglePattern)x.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState == ToggleState.On), "Select all checks rows");
            Invoke("DeselectAll");
            WaitUntil(() => !Control("DeleteSelected").Current.IsEnabled, "deselect disables deletion");
            Invoke("SelectAll");
            WaitUntil(() => Control("DeleteSelected").Current.IsEnabled, "selection restored before confirmation");

            // Exercise the actual confirmation UI. Cancel must not alter files.
            ConfirmDelete(false);
            Assert(NativeExists(first) && NativeExists(second), "No confirmation keeps both files");
            Invoke("DeselectAll");
            var toggles = GetToggles(grid);
            ((TogglePattern)toggles[0].GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            WaitUntil(() => Control("DeleteSelected").Current.IsEnabled, "single selection enables deletion");
            ConfirmDelete(true);
            Assert(NativeExists(first) ^ NativeExists(second), "Only the selected NUL file is deleted");
            Assert(File.Exists(Path.Combine(folder, "null")), "Normal null file is preserved");
            Assert(NativeExists(Path.Combine(folder, "nul.txt")), "nul.txt is preserved");
            Assert(Directory.Exists(Extended(Path.Combine(folder, "nul"))), "nul directory is preserved");
            Assert(File.Exists(Path.Combine(folder, "normal.txt")), "Normal file is preserved");

            Invoke("SelectAll");
            WaitUntil(() => Control("DeleteSelected").Current.IsEnabled, "remaining file can be selected");
            ConfirmDelete(true);
            Assert(!NativeExists(first) && !NativeExists(second), "Remaining selected NUL is deleted");
            Assert(!Control("DeleteSelected").Current.IsEnabled, "Deleted rows cannot be selected again");
            Invoke("NavActivity");
            Invoke("NavSettings");
            Invoke("NavNulCleanup");
            Assert(!Control("DeleteSelected").Current.IsEnabled, "Tool state survives navigation");

            ((WindowPattern)_window.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Assert(_app.WaitForExit(10000), "Window closes cleanly");
            Assert(_app.ExitCode == 0, "Application exits successfully");
            Assert(Directory.Exists(Path.Combine(_fixture, "app-data")), "Test settings/logs isolated to fixture");
            Console.WriteLine($"PASS: {_assertions} desktop assertions, real UI confirmation and permanent deletion.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine("Owned test fixture retained on failure: " + _fixture);
            return 1;
        }
        finally
        {
            if (_app is { HasExited: false })
            {
                // Kill only the application process started by this test.
                _app.Kill(entireProcessTree: true);
                _app.WaitForExit(10000);
            }
            _app?.Dispose();
            // Keep the small uniquely named fixture as auditable evidence. Never delete user folders.
        }
    }

    private static AutomationElement[] GetToggles(AutomationElement grid) =>
        grid.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox))
            .Cast<AutomationElement>().Where(x => x.Current.IsEnabled).ToArray();

    private static void ConfirmDelete(bool yes)
    {
        var control = Control("DeleteSelected");
        var invoke = (InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern);
        var task = Task.Run(() => invoke.Invoke());
        AutomationElement dialog;
        try { dialog = WaitForWindow(_app!.Id, "确认永久删除", TimeSpan.FromSeconds(15)); }
        catch
        {
            Console.Error.WriteLine("Delete Invoke task status: " + task.Status);
            if (task.IsFaulted) Console.Error.WriteLine(task.Exception);
            foreach (var top in AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, _app!.Id)).Cast<AutomationElement>())
                Console.Error.WriteLine("Process window: " + top.Current.Name);
            throw;
        }
        var buttons = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        var desiredId = yes ? "6" : "7"; // Standard Win32 IDYES / IDNO.
        var button = buttons.Cast<AutomationElement>().FirstOrDefault(x => x.Current.AutomationId == desiredId)
            ?? buttons.Cast<AutomationElement>().FirstOrDefault(x => yes ? x.Current.Name.StartsWith("是") || x.Current.Name.StartsWith("Yes") : x.Current.Name.StartsWith("否") || x.Current.Name.StartsWith("No"))
            ?? throw new Exception("Standard confirmation button missing.");
        if (!yes)
        {
            var focused = AutomationElement.FocusedElement;
            Assert(focused.Current.AutomationId == "7", "Confirmation defaults to No");
        }
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        if (!task.Wait(10000)) throw new Exception("Delete invocation did not return.");
        WaitUntil(() => Control("Scan").Current.IsEnabled, "delete completes");
    }

    private static void SetText(string id, string value) =>
        ((ValuePattern)Control(id).GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static void Invoke(string id)
    {
        var control = Control(id);
        if (control.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
            ((InvokePattern)pattern).Invoke();
        else if (control.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            ((SelectionItemPattern)selection).Select();
        else throw new Exception($"Control {id} is not invokable.");
    }

    private static AutomationElement Control(string id) => _window!.FindFirst(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, id)) ?? throw new Exception("Control missing: " + id);

    private static AutomationElement WaitForWindow(int processId, string nameContains, TimeSpan timeout)
    {
        AutomationElement? found = null;
        WaitUntil(() =>
        {
            if (_app is { HasExited: true }) throw new Exception("App exited before expected window; code=" + _app.ExitCode);
            var matches = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
            var windows = matches.Cast<AutomationElement>().ToList();
            foreach (var parent in matches.Cast<AutomationElement>())
                windows.AddRange(parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)).Cast<AutomationElement>());
            found = windows.FirstOrDefault(x => x.Current.Name.Contains(nameContains, StringComparison.Ordinal));
            return found is not null;
        }, "window " + nameContains, timeout);
        return found!;
    }

    private static void WaitUntil(Func<bool> condition, string description, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(20)))
        {
            if (condition()) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("Timed out: " + description);
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        _assertions++;
        Console.WriteLine("PASS: " + description);
    }

    private static string Extended(string path)
    {
        // Test paths already originate from our absolute unique fixture. GetFullPath on a
        // reserved leaf can normalize it to a device path before the prefix is applied.
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Expected an absolute local test-fixture path.");
        return @"\\?\" + path;
    }
    private static bool NativeExists(string path) => GetFileAttributesW(Extended(path)) != uint.MaxValue;
    private static void CreateNativeFile(string path)
    {
        var handle = CreateFileW(Extended(path), 0x40000000, 7, IntPtr.Zero, 1, 0x80, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
}
