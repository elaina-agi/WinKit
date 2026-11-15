using System.IO;
using System.Text.Json;

namespace WinKit.Desktop.Services;

public sealed class AppSettings
{
    public bool RememberLastFolder { get; set; } = true;
    public string LastFolder { get; set; } = string.Empty;
}

public sealed class SettingsStore
{
    private readonly string _settingsFile;
    public AppSettings Current { get; }
    public string DataDirectory { get; }
    public string? LoadWarning { get; }

    public SettingsStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        _settingsFile = Path.Combine(dataDirectory, "settings.json");
        try
        {
            Current = File.Exists(_settingsFile)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile)) ?? new AppSettings()
                : new AppSettings();
            Current.LastFolder ??= string.Empty;
        }
        catch (Exception exception)
        {
            Current = new AppSettings();
            LoadWarning = $"设置读取失败，已使用默认设置：{exception.Message}";
        }
    }

    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var temporaryPath = _settingsFile + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _settingsFile, true);
            return null;
        }
        catch (Exception exception) { return $"设置保存失败：{exception.Message}"; }
    }
}
