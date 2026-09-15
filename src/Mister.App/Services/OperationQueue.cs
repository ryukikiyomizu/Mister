using Mister.App.ViewModels;

namespace Mister.App.Services;

public sealed class OperationQueue
{
    private readonly object sync = new();
    private CancellationTokenSource? cancellation;
    private Task? running;

    public OperationState State { get; private set; } = OperationState.Idle;
    public bool HasPublishedPartialFile { get; private set; }
    public event Action<OperationState>? StateChanged;

    public Task StartAsync(Func<CancellationToken, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (sync)
        {
            if (State is not OperationState.Idle) throw new InvalidOperationException("Only one operation can run at a time.");
            cancellation = new CancellationTokenSource();
            SetState(OperationState.Running);
            running = RunAsync(operation, cancellation);
            return running;
        }
    }

    public async Task CancelAsync()
    {
        Task? pending;
        lock (sync)
        {
            if (State is OperationState.Idle or OperationState.Failed) return;
            SetState(OperationState.Cancelling);
            cancellation!.Cancel();
            pending = running;
        }
        if (pending is not null) await pending.ConfigureAwait(false);
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationTokenSource source)
    {
        try { await operation(source.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch { SetState(OperationState.Failed); throw; }
        finally
        {
            source.Dispose();
            lock (sync)
            {
                cancellation = null;
                running = null;
                HasPublishedPartialFile = false;
                SetState(OperationState.Idle);
            }
        }
    }

    private void SetState(OperationState value)
    {
        State = value;
        StateChanged?.Invoke(value);
    }
}
