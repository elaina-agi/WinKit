using System.Collections.ObjectModel;
using System.ComponentModel;
using WinKit.Core.NulCleanup;
using WinKit.Desktop.Infrastructure;
using WinKit.Desktop.Services;

namespace WinKit.Desktop.Tools.NulCleanup;

public sealed class NulCleanupViewModel : ObservableObject, IBackgroundWork
{
    private readonly NulCleanupService _service;
    private readonly IDesktopDialogs _dialogs;
    private readonly SettingsStore _settings;
    private readonly OperationJournal _journal;
    private CancellationTokenSource? _cancellation;
    private Task _activeWork = Task.CompletedTask;
    private ScanResult? _lastScan;
    private bool _acceptProgress;
    private bool _suppressSelectionUpdates;
    private int _scanVersion;
    private string _rootPath;
    private bool _isBusy;
    private string _statusTitle = "准备扫描";
    private string _statusDetail = "选择一个文件夹。只检查名字恰好是 nul 的普通文件，不跟随链接或目录联接。";
    private string _currentDirectory = string.Empty;
    private bool _hasNotice;
    private string _notice = string.Empty;
    private NulFileRow? _activeRow;

    public NulCleanupViewModel(NulCleanupService service, IDesktopDialogs dialogs, SettingsStore settings, OperationJournal journal)
    {
        _service = service;
        _dialogs = dialogs;
        _settings = settings;
        _journal = journal;
        _rootPath = settings.Current.RememberLastFolder ? settings.Current.LastFolder : string.Empty;
        BrowseCommand = new RelayCommand(Browse, () => !IsBusy);
        ScanCommand = new AsyncCommand(() => StartScanAsync(), () => !IsBusy && !string.IsNullOrWhiteSpace(RootPath), ReportUnexpectedError);
        CancelCommand = new RelayCommand(RequestCancellation, () => IsBusy && _cancellation is { IsCancellationRequested: false });
        SelectAllCommand = new RelayCommand(SelectAll, () => !IsBusy && Files.Any(row => row.CanSelect && !row.IsSelected));
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !IsBusy && SelectedCount > 0);
        DeleteSelectedCommand = new AsyncCommand(StartDeleteAsync, () => !IsBusy && _lastScan is not null && SelectedCount > 0, ReportUnexpectedError);
        CopyPathCommand = new RelayCommand(CopyPath, () => ActiveRow is not null);
        ShowIssuesCommand = new RelayCommand(ShowIssues, () => !IsBusy && _lastScan is not null);
    }

    public ObservableCollection<NulFileRow> Files { get; } = [];
    public string RootPath
    {
        get => _rootPath;
        set
        {
            if (IsBusy || !SetProperty(ref _rootPath, value)) return;
            if (_lastScan is not null || Files.Count > 0)
            {
                ReplaceFiles([]);
                _lastScan = null;
                HasNotice = false;
                StatusTitle = "路径已更改，请重新扫描";
                StatusDetail = "此前结果已清空，避免对旧目录执行操作。";
            }
            OnPropertyChanged(nameof(ScannedScope));
            RefreshCommands();
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            RefreshCommands();
        }
    }
    public bool IsIdle => !IsBusy;
    public string StatusTitle { get => _statusTitle; private set => SetProperty(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public string CurrentDirectory { get => _currentDirectory; private set => SetProperty(ref _currentDirectory, value); }
    public bool HasNotice { get => _hasNotice; private set => SetProperty(ref _hasNotice, value); }
    public string Notice { get => _notice; private set => SetProperty(ref _notice, value); }
    public int SelectedCount => Files.Count(row => row.IsSelected);
    public int FileCount => Files.Count;
    public string SelectionSummary => $"找到 {FileCount} 项 · 已选择 {SelectedCount} 项";
    public string ScannedScope => _lastScan is null ? "尚未扫描" : $"扫描范围：{_lastScan.RootPath}";
    public bool IsEmpty => Files.Count == 0;
    public ScanResult? LastScan => _lastScan;
    public Task ActiveWork => _activeWork;
    public NulFileRow? ActiveRow
    {
        get => _activeRow;
        set { if (SetProperty(ref _activeRow, value)) CopyPathCommand.RaiseCanExecuteChanged(); }
    }

    public RelayCommand BrowseCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncCommand DeleteSelectedCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand ShowIssuesCommand { get; }

    private void Browse()
    {
        try { var selected = _dialogs.BrowseFolder(RootPath); if (selected is not null) RootPath = selected; }
        catch (Exception exception) { _dialogs.ShowMessage("无法选择文件夹", exception.Message); }
    }

    public Task StartScanAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(RootPath)) return Task.CompletedTask;
        var root = RootPath.Trim();
        _activeWork = ScanCoreAsync(root);
        return _activeWork;
    }

    private async Task ScanCoreAsync(string root)
    {
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        _lastScan = null;
        ReplaceFiles([]);
        HasNotice = false;
        StatusTitle = "正在扫描";
        StatusDetail = "后台扫描进行中；可以取消。不会修改文件。";
        CurrentDirectory = root;
        _acceptProgress = true;
        var version = ++_scanVersion;
        OnPropertyChanged(nameof(ScannedScope));
        try
        {
            var progress = new Progress<ScanProgress>(update =>
            {
                if (version != _scanVersion || !_acceptProgress || !IsBusy || _cancellation is null || _cancellation.IsCancellationRequested) return;
                StatusDetail = $"已检查 {update.DirectoriesVisited:N0} 个目录 · 发现 {update.FilesFound:N0} 个 NUL 文件";
                CurrentDirectory = update.CurrentDirectory;
            });
            // The Core service stays alive for this module: delete checks its saved scan root identity.
            var result = await _service.ScanAsync(root, progress, _cancellation.Token);
            _acceptProgress = false;
            _lastScan = result;
            ReplaceFiles(result.Files.Select(candidate => new NulFileRow(candidate)));
            StatusTitle = result.WasCancelled ? "扫描已取消" : result.Issues.Count > 0 ? "扫描不完整" : "扫描完成";
            StatusDetail = $"找到 {result.Files.Count:N0} 个 NUL 文件 · 跳过 {result.SkippedReparsePoints:N0} 个重解析点 · {result.Issues.Count:N0} 个扫描问题";
            Notice = result.WasCancelled
                ? "扫描已取消，列表只包含取消前发现的文件，不能据此判断整个目录没有其他问题。"
                : result.Issues.Count > 0
                    ? "部分路径未能检查，扫描结果不完整。请查看扫描问题；已列出的文件仍需由你勾选确认。"
                    : result.SkippedReparsePoints > 0
                        ? "为避免跨目录或循环扫描，已主动跳过链接、目录联接等重解析点；不会检查它们指向的位置。"
                        : string.Empty;
            HasNotice = Notice.Length > 0;
            CurrentDirectory = string.Empty;
            OnPropertyChanged(nameof(ScannedScope));
            if (_settings.Current.RememberLastFolder)
            {
                _settings.Current.LastFolder = result.RootPath;
                var warning = _settings.Save();
                if (warning is not null) await _journal.WriteAsync("设置", warning, level: "警告");
            }
            await _journal.WriteAsync("NUL 扫描", $"{StatusTitle}；{StatusDetail}", result.RootPath,
                result.WasCancelled || result.Issues.Count > 0 ? "警告" : "信息");
            foreach (var issue in result.Issues)
                await _journal.WriteAsync("扫描问题", issue.Message, issue.Path, "警告");
        }
        catch (OperationCanceledException)
        {
            StatusTitle = "扫描已取消";
            StatusDetail = "扫描未返回完整结果。可以重新扫描。";
            Notice = "本次扫描被取消，未返回可操作结果。";
            HasNotice = true;
            await _journal.WriteAsync("NUL 扫描", "扫描已取消，未返回结果。", root, "警告");
        }
        catch (Exception exception)
        {
            StatusTitle = "扫描失败";
            StatusDetail = exception.Message;
            Notice = "未完成扫描。请检查路径和访问权限；本工具不会自动提权。";
            HasNotice = true;
            await _journal.WriteAsync("NUL 扫描", exception.Message, root, "错误");
        }
        finally
        {
            _acceptProgress = false;
            _cancellation.Dispose();
            _cancellation = null;
            CurrentDirectory = string.Empty;
            IsBusy = false;
            RefreshCommands();
        }
    }

    public void RequestCancellation()
    {
        if (_cancellation is not { IsCancellationRequested: false }) return;
        _cancellation.Cancel();
        StatusTitle = "正在安全取消";
        StatusDetail = "等待当前文件操作结束。已完成的永久删除无法撤销。";
        CancelCommand.RaiseCanExecuteChanged();
    }

    private Task StartDeleteAsync()
    {
        if (IsBusy || _lastScan is null) return Task.CompletedTask;
        var selected = Files.Where(row => row.IsSelected && row.CanSelect).ToArray();
        if (selected.Length == 0 || !_dialogs.ConfirmPermanentDelete(selected.Length, _lastScan.RootPath)) return Task.CompletedTask;
        _activeWork = DeleteCoreAsync(_lastScan.RootPath, selected);
        return _activeWork;
    }

    private async Task DeleteCoreAsync(string scannedRoot, NulFileRow[] selected)
    {
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusTitle = "正在删除所选文件";
        StatusDetail = $"正在重新验证并处理 {selected.Length:N0} 个文件。删除不经过回收站。";
        _suppressSelectionUpdates = true;
        try
        {
            var results = await _service.DeleteAsync(scannedRoot, selected.Select(row => row.Candidate).ToArray(), _cancellation.Token);
            var byPath = selected.ToDictionary(row => row.FullPath, StringComparer.Ordinal);
            foreach (var result in results)
            {
                if (byPath.TryGetValue(result.File.FullPath, out var row)) row.Apply(result);
                await _journal.WriteAsync("NUL 删除", $"{StatusText(result.Status)}：{result.Message}", result.File.FullPath,
                    result.Status is DeleteStatus.Failed or DeleteStatus.Refused ? "警告" : "信息");
            }
            foreach (var row in selected) row.IsSelected = false;
            var deleted = results.Count(result => result.Status == DeleteStatus.Deleted);
            var missing = results.Count(result => result.Status == DeleteStatus.AlreadyMissing);
            var failed = results.Count(result => result.Status is DeleteStatus.Failed or DeleteStatus.Refused);
            var notProcessed = selected.Length - results.Count;
            StatusTitle = _cancellation.IsCancellationRequested ? "删除已取消" : failed > 0 || notProcessed > 0 ? "删除完成，部分未成功" : "删除完成";
            StatusDetail = $"已删除 {deleted} 项 · 已不存在 {missing} 项 · 失败 / 拒绝 {failed} 项 · 未处理 {notProcessed} 项";
            Notice = failed > 0 || notProcessed > 0
                ? "未成功的项没有标为已删除。悬停处理状态可查看原因；已拒绝的文件需要重新扫描后再操作。"
                : "已删除或已不存在的文件不可再次勾选。永久删除无法撤销。";
            HasNotice = true;
            await _journal.WriteAsync("删除汇总", $"{StatusTitle}；{StatusDetail}", scannedRoot, failed > 0 || notProcessed > 0 ? "警告" : "信息");
        }
        catch (OperationCanceledException)
        {
            foreach (var row in selected) row.RequireRescan("核心未返回完整逐项结果；此文件可能已处理，请重新扫描确认。");
            StatusTitle = "删除已取消，请重新扫描";
            StatusDetail = "未返回完整逐项结果；部分文件可能已删除。重新扫描后再操作。";
            _lastScan = null;
            HasNotice = true;
            Notice = StatusDetail;
            await _journal.WriteAsync("NUL 删除", StatusDetail, scannedRoot, "警告");
        }
        catch (Exception exception)
        {
            foreach (var row in selected) row.RequireRescan("核心未返回完整逐项结果；此文件可能已处理，请重新扫描确认。");
            StatusTitle = "删除未完成，请重新扫描";
            StatusDetail = exception.Message;
            _lastScan = null;
            HasNotice = true;
            Notice = "未返回完整逐项结果，部分文件可能已处理。请重新扫描；不会把未知结果标为成功。";
            await _journal.WriteAsync("NUL 删除", exception.Message, scannedRoot, "错误");
        }
        finally
        {
            _acceptProgress = false;
            _cancellation.Dispose();
            _cancellation = null;
            _suppressSelectionUpdates = false;
            SelectionChanged();
            IsBusy = false;
            OnPropertyChanged(nameof(ScannedScope));
            RefreshCommands();
        }
    }

    private static string StatusText(DeleteStatus status) => status switch
    {
        DeleteStatus.Deleted => "已删除", DeleteStatus.AlreadyMissing => "已不存在", DeleteStatus.Refused => "拒绝删除", _ => "删除失败"
    };

    public void SelectAll()
    {
        if (IsBusy) return;
        _suppressSelectionUpdates = true;
        try { foreach (var row in Files) if (row.CanSelect) row.IsSelected = true; }
        finally { _suppressSelectionUpdates = false; SelectionChanged(); }
    }
    public void ClearSelection()
    {
        if (IsBusy) return;
        _suppressSelectionUpdates = true;
        try { foreach (var row in Files) row.IsSelected = false; }
        finally { _suppressSelectionUpdates = false; SelectionChanged(); }
    }
    private void CopyPath() { if (ActiveRow is not null) _dialogs.CopyText(ActiveRow.FullPath); }
    private void ShowIssues() { if (_lastScan is not null) _dialogs.ShowScanIssues(_lastScan.Issues, _lastScan.SkippedReparsePoints, _lastScan.WasCancelled); }
    private void ReportUnexpectedError(Exception exception) => _dialogs.ShowMessage("操作未完成", exception.Message);

    private void ReplaceFiles(IEnumerable<NulFileRow> rows)
    {
        foreach (var row in Files) row.PropertyChanged -= RowChanged;
        Files.Clear();
        ActiveRow = null;
        foreach (var row in rows) { row.PropertyChanged += RowChanged; Files.Add(row); }
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(IsEmpty));
        SelectionChanged();
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressSelectionUpdates && (e.PropertyName is nameof(NulFileRow.IsSelected) or nameof(NulFileRow.CanSelect))) SelectionChanged();
    }
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        BrowseCommand.RaiseCanExecuteChanged(); ScanCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged(); ClearSelectionCommand.RaiseCanExecuteChanged(); DeleteSelectedCommand.RaiseCanExecuteChanged();
        CopyPathCommand.RaiseCanExecuteChanged(); ShowIssuesCommand.RaiseCanExecuteChanged();
    }
}
