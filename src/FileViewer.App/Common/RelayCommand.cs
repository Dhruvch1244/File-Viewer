using System.Windows.Input;

namespace FileViewer.App.Common;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public static RelayCommand Create(Action execute, Func<bool>? canExecute = null) =>
        new(_ => execute(), canExecute is null ? null : _ => canExecute());
}
