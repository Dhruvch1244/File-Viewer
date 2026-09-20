using System.Windows.Input;

namespace FileViewer.App.Common;

/// <summary>Async command that disables re-entrancy while running; unhandled exceptions from <paramref name="execute"/> propagate to <c>Application.DispatcherUnhandledException</c> rather than being silently swallowed.</summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>
/// <see cref="AsyncRelayCommand"/> for a command driven by a bound item (a recent file path, a tab)
/// rather than by no parameter at all.
/// </summary>
public sealed class AsyncRelayCommand<T>(Func<T, Task> execute, Func<T?, bool>? canExecute = null) : ICommand
    where T : class
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke(parameter as T) ?? true);

    public async void Execute(object? parameter)
    {
        if (parameter is not T typed) return;

        _isExecuting = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(typed);
        }
        finally
        {
            _isExecuting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
