namespace Sefirah.Utils;

/// <summary>Serializes work and retains the latest event received during an active pass.</summary>
public sealed class CoalescingAsyncWork
{
    private readonly object gate = new();
    private Func<Task>? pending;
    private TaskCompletionSource? completion;

    public Task RunAsync(Func<Task> action)
    {
        TaskCompletionSource batch;
        bool start;
        lock (gate)
        {
            pending = action;
            start = completion is null;
            batch = completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (start) _ = DrainAsync(batch);
        return batch.Task;
    }

    private async Task DrainAsync(TaskCompletionSource batch)
    {
        Exception? failure = null;
        while (true)
        {
            Func<Task>? action;
            lock (gate)
            {
                action = pending;
                pending = null;
                if (action is null) completion = null;
            }
            if (action is null) break;
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is null) batch.TrySetResult();
        else batch.TrySetException(failure);
    }
}
