namespace WinKit.Desktop.Infrastructure;

// Future tools can participate in safe window shutdown without adopting scanner UI.
public interface IBackgroundWork
{
    bool IsBusy { get; }
    Task ActiveWork { get; }
    void RequestCancellation();
}
