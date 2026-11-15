using System.Windows.Controls;
using WinKit.Desktop.Infrastructure;
using WinKit.Desktop.Pages;
using WinKit.Desktop.Tools.NulCleanup;

namespace WinKit.Desktop.Shell;

public sealed class NavigationItem : ObservableObject
{
    private bool _isSelected;
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string IconGeometry { get; init; }
    public required Func<UserControl> CreateView { get; init; }
    public RelayCommand NavigateCommand { get; set; } = null!;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public string AutomationId => Id switch { "nul-cleanup" => "NavNulCleanup", "activity" => "NavActivity", "settings" => "NavSettings", _ => "NavTool_" + Id };
}

public sealed record NavigationGroup(string Category, IReadOnlyList<NavigationItem> Items);

public sealed class ShellViewModel : ObservableObject
{
    private UserControl? _currentView;
    private string _currentTitle = string.Empty;
    private readonly IReadOnlyList<IBackgroundWork> _workOwners;
    public bool HasActiveWork => _workOwners.Any(work => work.IsBusy);
    public async Task CancelAndWaitAsync()
    {
        var active = _workOwners.Where(work => work.IsBusy).ToArray();
        foreach (var work in active) work.RequestCancellation();
        await Task.WhenAll(active.Select(work => work.ActiveWork));
    }
    public NulCleanupViewModel NulCleanup { get; }
    public IReadOnlyList<NavigationItem> ToolNavigation { get; }
    public IReadOnlyList<NavigationGroup> ToolGroups { get; }
    public IReadOnlyList<NavigationItem> UtilityNavigation { get; }
    public UserControl? CurrentView { get => _currentView; private set => SetProperty(ref _currentView, value); }
    public string CurrentTitle { get => _currentTitle; private set => SetProperty(ref _currentTitle, value); }

    public ShellViewModel(ToolRegistry registry, NulCleanupViewModel nulCleanup, ActivityViewModel activity, SettingsViewModel settings)
    {
        NulCleanup = nulCleanup;
        _workOwners = registry.Tools.Select(tool => tool.ViewModel).OfType<IBackgroundWork>().ToArray();
        ToolNavigation = registry.Tools.Select(tool => new NavigationItem
        {
            Id = tool.Id, Title = tool.Title, IconGeometry = tool.IconGeometry, CreateView = tool.CreateView
        }).ToArray();
        ToolGroups = registry.Tools.GroupBy(tool => tool.Category).Select(group => new NavigationGroup(group.Key,
            group.Select(tool => ToolNavigation.First(item => item.Id == tool.Id)).ToArray())).ToArray();
        UtilityNavigation = [
            new NavigationItem { Id = "activity", Title = "操作记录", IconGeometry = "M 5,3 L 20,3 L 20,22 L 5,22 Z M 9,8 L 16,8 M 9,12 L 16,12 M 9,16 L 16,16", CreateView = () => new ActivityView { DataContext = activity } },
            new NavigationItem { Id = "settings", Title = "设置", IconGeometry = "M 4,7 L 21,7 M 4,17 L 21,17 M 9,3 L 9,11 M 16,13 L 16,21", CreateView = () => new SettingsView { DataContext = settings } }
        ];
        foreach (var item in ToolNavigation.Concat(UtilityNavigation)) item.NavigateCommand = new RelayCommand(() => Navigate(item.Id));
        Navigate(ToolNavigation[0].Id);
    }

    public void Navigate(string id)
    {
        var item = ToolNavigation.Concat(UtilityNavigation).FirstOrDefault(candidate => candidate.Id == id);
        if (item is null) return;
        foreach (var candidate in ToolNavigation.Concat(UtilityNavigation)) candidate.IsSelected = candidate == item;
        CurrentView = item.CreateView();
        CurrentTitle = item.Title;
    }
}
