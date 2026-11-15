using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WinKit.Desktop.Infrastructure;

namespace WinKit.Desktop.Services;

/// <summary>One shared, live theme for all toolbox pages; preference is local to this user.</summary>
public sealed class ThemeService : ObservableObject
{
    private readonly SettingsStore _settings;
    private bool _isDark;
    private string _saveStatus = "主题切换立即生效，并记住你的选择。";

    public ThemeService(SettingsStore settings)
    {
        _settings = settings;
        _isDark = settings.Current.UseDarkTheme;
        ToggleCommand = new RelayCommand(() => IsDark = !IsDark);
        ApplyPalette(_isDark);
    }

    public bool IsDark
    {
        get => _isDark;
        set
        {
            if (_isDark == value) return;
            Application.Current.Dispatcher.VerifyAccess();
            // Load first: an invalid resource must not persist a preference it could not apply.
            ApplyPalette(value);
            _isDark = value;
            _settings.Current.UseDarkTheme = value;
            SaveStatus = _settings.Save() is { } warning
                ? $"主题已切换，但未能记住选择：{warning}"
                : $"已切换为{(value ? "夜间" : "浅色")}主题，下次启动继续使用。";
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToggleLabel));
        }
    }

    public string ToggleLabel => IsDark ? "切换浅色" : "切换夜间";
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public RelayCommand ToggleCommand { get; }

    private static void ApplyPalette(bool dark)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"/WinKit;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dictionary);
        else merged[0] = dictionary;
    }

    /// <summary>Match native title bars where Windows supports it; no system settings are changed.</summary>
    public void AttachWindow(Window window)
    {
        void RefreshTitleBar()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var enabled = IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
            { /* An unsupported title-bar effect must never block the toolbox. */ }
        }
        void ThemeChanged(object? sender, PropertyChangedEventArgs e)
        { if (e.PropertyName == nameof(IsDark)) RefreshTitleBar(); }
        void SourceReady(object? sender, EventArgs e) => RefreshTitleBar();
        void WindowClosed(object? sender, EventArgs e)
        {
            PropertyChanged -= ThemeChanged;
            window.SourceInitialized -= SourceReady;
            window.Closed -= WindowClosed;
        }
        PropertyChanged += ThemeChanged;
        window.SourceInitialized += SourceReady;
        window.Closed += WindowClosed;
        RefreshTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
