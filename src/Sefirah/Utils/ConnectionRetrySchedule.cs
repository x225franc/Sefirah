namespace Sefirah.Utils;

/// <summary>Shared by discovery and watchdog, so announcements cannot bypass retry backoff.</summary>
public sealed class ConnectionRetrySchedule
{
    private readonly object gate = new();
    private readonly Dictionary<string, (int Failures, long Due)> entries = [];

    public bool IsDue(string deviceId, long now)
    {
        lock (gate) return !entries.TryGetValue(deviceId, out var entry) || now >= entry.Due;
    }

    public void Failed(string deviceId, long now)
    {
        lock (gate)
        {
            var failures = entries.TryGetValue(deviceId, out var previous) ? Math.Min(previous.Failures + 1, 6) : 1;
            var delay = Math.Min(15_000L << (failures - 1), 300_000L);
            entries[deviceId] = (failures, now + delay);
        }
    }

    public void Reset(string deviceId) { lock (gate) entries.Remove(deviceId); }
    public void ResetAll() { lock (gate) entries.Clear(); }
}
