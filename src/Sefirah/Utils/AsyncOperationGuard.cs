namespace Sefirah.Utils;

/// <summary>Contains failures at event-driven boundaries, including before the first await.</summary>
public static class AsyncOperationGuard
{
    public static async Task<bool> RunAsync(Func<Task> operation, Action<Exception> report)
    {
        try { await operation().ConfigureAwait(false); return true; }
        catch (Exception error) { report(error); return false; }
    }
}
