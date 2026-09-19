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

    /// <summary>Typed overload for commands driven by a bound item (a tab, a row, ...) rather than by no parameter at all.</summary>
    public static RelayCommand Create<T>(Action<T?> execute, Predicate<T?>? canExecute = null) where T : class =>
        new(parameter => execute(parameter as T), canExecute is null ? null : parameter => canExecute(parameter as T));
}
