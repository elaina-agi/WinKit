using System.Windows.Input;

namespace WinKit.Desktop.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null) : ICommand
{
    private bool _executing;
    public bool CanExecute(object? parameter) => !_executing && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _executing = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        catch (Exception exception) { onError?.Invoke(exception); }
        finally { _executing = false; RaiseCanExecuteChanged(); }
    }
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
