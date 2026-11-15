using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinKit.Desktop.Services;
using WinKit.Desktop.Shell;

namespace WinKit.Desktop.Testing;

// Explicit scan-only UI test entry. This is deliberately not a delete automation channel.
internal static class UiSmokeRunner
{
    public static async Task<int> RunAsync(MainWindow window, ShellViewModel shell, string fixtureRoot, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var listener = new BindingTraceListener();
        var previousTraceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        try
        {
            // Re-create the initial module after tracing starts so its bindings are covered too.
            shell.Navigate("nul-cleanup");
            var vm = shell.NulCleanup;
            vm.RootPath = fixtureRoot;
            await vm.StartScanAsync();
            await SettleAsync(window);
            var defaultSelected = vm.SelectedCount;
            var expectedSelectable = vm.Files.Count(row => row.CanSelect);
            vm.SelectAll();
            var afterSelectAll = vm.SelectedCount;
            vm.ClearSelection();
            var afterClearSelection = vm.SelectedCount;
            shell.Theme.IsDark = false;
            var lightKeys = Application.Current.Resources.MergedDictionaries[0].Keys.Cast<string>().Order().ToArray();
            var lightContrast = PaletteContrastIsReadable();
            var lightBackground = window.Background;
            await CaptureAsync(window, Path.Combine(outputDirectory, "nul-cleanup.png"));
            shell.Navigate("activity");
            await CaptureAsync(window, Path.Combine(outputDirectory, "activity.png"));
            var activityCreated = shell.CurrentTitle == "操作记录" && shell.CurrentView is not null;
            shell.Navigate("settings");
            await CaptureAsync(window, Path.Combine(outputDirectory, "settings.png"));
            var settingsCreated = shell.CurrentTitle == "设置" && shell.CurrentView is not null;
            shell.Navigate("nul-cleanup");
            var existingView = shell.CurrentView;
            shell.Theme.IsDark = true;
            await SettleAsync(window);
            var darkKeys = Application.Current.Resources.MergedDictionaries[0].Keys.Cast<string>().Order().ToArray();
            var paletteKeysMatch = lightKeys.SequenceEqual(darkKeys);
            var darkContrast = PaletteContrastIsReadable();
            var themeAppliedLive = window.Background != lightBackground && existingView == shell.CurrentView;
            var themePreservesResults = vm.FileCount == expectedSelectable && vm.SelectedCount == 0;
            var persistence = new SettingsStore(Path.Combine(outputDirectory, "isolated-app-data"));
            var themePersisted = persistence.Current.UseDarkTheme;
            await CaptureAsync(window, Path.Combine(outputDirectory, "nul-cleanup-dark.png"));
            // Exercise selected checkboxes, row highlight, and the destructive button without deleting.
            vm.SelectAll();
            if (vm.Files.Count > 0) vm.ActiveRow = vm.Files[0];
            await CaptureAsync(window, Path.Combine(outputDirectory, "nul-cleanup-dark-selected.png"));
            vm.ClearSelection();
            shell.Navigate("activity");
            await CaptureAsync(window, Path.Combine(outputDirectory, "activity-dark.png"));
            shell.Navigate("settings");
            await CaptureAsync(window, Path.Combine(outputDirectory, "settings-dark.png"));
            shell.Navigate("nul-cleanup");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await CaptureAsync(window, Path.Combine(outputDirectory, "nul-cleanup-dark-minimum.png"));
            shell.Theme.IsDark = false;
            await CaptureAsync(window, Path.Combine(outputDirectory, "nul-cleanup-minimum.png"));

            var errors = listener.Messages;
            var passed = vm.LastScan is { WasCancelled: false } && !vm.IsBusy && defaultSelected == 0 &&
                afterSelectAll == expectedSelectable && afterClearSelection == 0 && activityCreated && settingsCreated && errors.Count == 0 &&
                paletteKeysMatch && lightContrast && darkContrast && themeAppliedLive && themePreservesResults && themePersisted;
            var state = new
            {
                Passed = passed,
                ScanOnly = true,
                NoDeletionInvoked = true,
                FixtureRoot = fixtureRoot,
                vm.StatusTitle, vm.StatusDetail, vm.FileCount,
                DefaultSelected = defaultSelected, SelectedAfterSelectAll = afterSelectAll, SelectedAfterClear = afterClearSelection,
                ExpectedSelectable = expectedSelectable,
                ActivityPageCreated = activityCreated, SettingsPageCreated = settingsCreated,
                Issues = vm.LastScan?.Issues,
                SkippedReparsePoints = vm.LastScan?.SkippedReparsePoints,
                Files = vm.Files.Select(row => new { row.FullPath, row.SizeText, row.Status, row.IsSelected, row.CanSelect }),
                PaletteKeysMatch = paletteKeysMatch, LightContrastReadable = lightContrast, DarkContrastReadable = darkContrast,
                ThemeAppliedLive = themeAppliedLive, ThemePreservesResults = themePreservesResults, ThemePersisted = themePersisted,
                BindingErrors = errors
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "smoke-state.json"), JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            return passed ? 0 : 1;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = previousTraceLevel;
        }
    }

    private static bool PaletteContrastIsReadable()
    {
        double Luminance(string key)
        {
            var color = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            double Linear(byte component)
            {
                var channel = component / 255.0;
                return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        bool Readable(string foreground, string background)
        {
            var a = Luminance(foreground); var b = Luminance(background);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05) >= 4.5;
        }
        return Readable("TextBrush", "CanvasBrush") && Readable("TextBrush", "SurfaceBrush") &&
            Readable("MutedBrush", "CanvasBrush") && Readable("MutedBrush", "SurfaceBrush") &&
            Readable("NoticeTextBrush", "NoticeBackgroundBrush");
    }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        await Task.Delay(120); // Test-only: allow composition/virtualized containers to finish a frame.
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
    }

    private static async Task CaptureAsync(Window window, string filePath)
    {
        await SettleAsync(window);
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(content);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY)), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Render the Window visual to retain its background and inherited control state,
        // but use client-area dimensions so non-client chrome creates no empty bottom strip.
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }

    private sealed class BindingTraceListener : TraceListener
    {
        private readonly StringBuilder _pending = new();
        public List<string> Messages { get; } = [];
        public override void Write(string? message) => _pending.Append(message);
        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            if (_pending.Length > 0) Messages.Add(_pending.ToString());
            _pending.Clear();
        }
    }
}
