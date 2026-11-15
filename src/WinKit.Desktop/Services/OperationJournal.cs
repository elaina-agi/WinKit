using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using WinKit.Desktop.Infrastructure;

namespace WinKit.Desktop.Services;

public sealed record OperationEntry(DateTimeOffset Timestamp, string Level, string Operation, string Message, string? Path)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss");
}

public sealed class OperationJournal : ObservableObject
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string _persistenceStatus = "操作记录保存在本机，不上传网络。";
    public string LogPath { get; }
    public ObservableCollection<OperationEntry> Entries { get; } = [];
    public string PersistenceStatus { get => _persistenceStatus; private set => SetProperty(ref _persistenceStatus, value); }

    public OperationJournal(string dataDirectory) => LogPath = System.IO.Path.Combine(dataDirectory, "operations.jsonl");

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            // Keep only a bounded tail in memory; the journal remains append-only on disk.
            var tail = new Queue<OperationEntry>();
            using var reader = new StreamReader(new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true));
            var invalidLines = 0;
            while (await reader.ReadLineAsync() is { } line)
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<OperationEntry>(line);
                    if (entry is null) continue;
                    tail.Enqueue(entry);
                    if (tail.Count > 1000) tail.Dequeue();
                }
                catch (JsonException) { invalidLines++; }
            }
            foreach (var entry in tail) Entries.Insert(0, entry);
            if (invalidLines > 0) PersistenceStatus = $"已忽略 {invalidLines} 条损坏的历史记录；其他记录正常。";
        }
        catch (Exception exception) { PersistenceStatus = $"历史记录读取失败：{exception.Message}。不影响扫描和删除。"; }
    }

    public async Task WriteAsync(string operation, string message, string? path = null, string level = "信息")
    {
        var entry = new OperationEntry(DateTimeOffset.Now, level, operation, message, path);
        await OnUiAsync(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > 1000) Entries.RemoveAt(Entries.Count - 1);
        });
        await _writeGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            await File.AppendAllTextAsync(LogPath, JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch (Exception exception)
        {
            await OnUiAsync(() => PersistenceStatus = $"日志写入失败：{exception.Message}。当前记录仍可查看，不影响主操作。");
        }
        finally { _writeGate.Release(); }
    }

    private static Task OnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { action(); return Task.CompletedTask; }
        return dispatcher.InvokeAsync(action).Task;
    }
}
