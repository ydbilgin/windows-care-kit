using System.Diagnostics;
using System.Windows.Input;

namespace WindowsCareKit.App.Mvvm;

/// <summary>
/// An <see cref="ICommand"/> for asynchronous work. Unlike wrapping <c>async () =&gt; ...</c> in a plain
/// <see cref="RelayCommand"/> (which produces an <c>async void</c> lambda whose post-await exception escapes to
/// the dispatcher as unhandled), this type OWNS the async void boundary: it awaits the callback inside a
/// try/catch so a fault is observed and routed to <paramref name="onError"/> (or traced) instead of crashing
/// the process, and it guards against re-entrancy so a second invocation while one is in flight is ignored
/// (never double-executed). Finding G3.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isRunning;

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
        : this(_ => (execute ?? throw new ArgumentNullException(nameof(execute)))(), canExecute, onError) { }

    /// <summary>False while a run is in flight — single-execution-at-a-time (no double execution).</summary>
    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_isRunning)
            return;

        _isRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await _execute(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (_onError is not null)
                _onError(ex);
            else
                Trace.TraceError("AsyncRelayCommand callback faulted: " + ex);
        }
        finally
        {
            _isRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
