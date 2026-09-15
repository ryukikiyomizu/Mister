using System.Windows.Input;

namespace Mister.App.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<CancellationToken, Task> execute;
    private readonly Func<bool>? canExecute;
    private int running;

    public AsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        this.execute = execute;
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => Volatile.Read(ref running) != 0;

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!(canExecute?.Invoke() ?? true)
            || Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return;
        }

        if (!(canExecute?.Invoke() ?? true))
        {
            Volatile.Write(ref running, 0);
            RaiseCanExecuteChanged();
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await execute(cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Volatile.Write(ref running, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
