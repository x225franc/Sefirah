using Sefirah.Utils;

internal static class RecoveryChecks
{
    public static async Task RunAsync()
    {
        var work = new CoalescingAsyncWork();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var first = work.RunAsync(async () => { calls.Add("suspend"); await release.Task; });
        Task latest = first;
        for (var i = 0; i < 100; i++)
            latest = work.RunAsync(() => { calls.Add("resume"); return Task.CompletedTask; });
        Require(calls.SequenceEqual(new[] { "suspend" }), "Recovery overlapped suspension");
        release.SetResult();
        await Task.WhenAll(first, latest).WaitAsync(TimeSpan.FromSeconds(5));
        Require(calls.SequenceEqual(new[] { "suspend", "resume" }), "Resume was lost or the burst multiplied recovery passes");

        // A new event during the second pass must also be retained.
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = work.RunAsync(async () => { calls.Add("network"); await secondRelease.Task; });
        var queued = work.RunAsync(() => { calls.Add("resume-again"); return Task.CompletedTask; });
        secondRelease.SetResult();
        await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(5));
        Require(calls.Last() == "resume-again", "A later resume was lost");

        // A cancelled old service generation must not strand work from the new generation.
        var failRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = work.RunAsync(async () => { await failRelease.Task; throw new OperationCanceledException(); });
        var recovered = false;
        var afterCancel = work.RunAsync(() => { recovered = true; return Task.CompletedTask; });
        failRelease.SetResult();
        try { await Task.WhenAll(cancelled, afterCancel).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { }
        Require(recovered, "Cancellation discarded pending recovery");
        await work.RunAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS: resume during suspension, 100-event coalescing, serial recovery, cancellation and restart.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
