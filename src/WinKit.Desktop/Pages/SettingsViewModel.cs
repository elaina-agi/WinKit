using WinKit.Desktop.Infrastructure;
using WinKit.Desktop.Services;

namespace WinKit.Desktop.Pages;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly IDesktopDialogs _dialogs;
    private readonly OperationJournal _journal;
    private string _saveStatus;
    public ThemeService Theme { get; }
    public SettingsViewModel(SettingsStore settings, IDesktopDialogs dialogs, OperationJournal journal, ThemeService theme)
    {
        _settings = settings;
        Theme = theme;
        _dialogs = dialogs;
        _journal = journal;
        _saveStatus = settings.LoadWarning ?? "设置在本机保存，更改后立即生效。";
        OpenLogFolderCommand = new RelayCommand(() => _dialogs.OpenDirectory(_settings.DataDirectory));
        CopyLogPathCommand = new RelayCommand(() => _dialogs.CopyText(_journal.LogPath));
    }
    public bool RememberLastFolder
    {
        get => _settings.Current.RememberLastFolder;
        set
        {
            if (_settings.Current.RememberLastFolder == value) return;
            _settings.Current.RememberLastFolder = value;
            if (!value) _settings.Current.LastFolder = string.Empty;
            OnPropertyChanged();
            SaveStatus = _settings.Save() ?? "设置已保存。";
        }
    }
    public string LogPath => _journal.LogPath;
    public string SaveStatus { get => _saveStatus; private set => SetProperty(ref _saveStatus, value); }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand CopyLogPathCommand { get; }
}

public sealed class ActivityViewModel(OperationJournal journal, IDesktopDialogs dialogs)
{
    public OperationJournal Journal { get; } = journal;
    public RelayCommand CopyLogPathCommand { get; } = new(() => dialogs.CopyText(journal.LogPath));
}
