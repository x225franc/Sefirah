namespace Sefirah.Utils;

/// <summary>Lets a later-starting desktop learn our endpoint without a broadcast/reply loop.</summary>
public sealed class DiscoveryReplyGate
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> lastReplies = [];

    public bool TryReply(string localId, string remoteId, string? remoteType, string address, long now)
    {
        // Only the non-initiating desktop replies. The lower-ID peer then opens one connection.
        if (!DesktopPeerPolicy.IsDesktop(remoteType) || string.CompareOrdinal(localId, remoteId) <= 0)
            return false;
        var key = remoteId + "@" + address;
        lock (gate)
        {
            if (lastReplies.TryGetValue(key, out var previous) && now - previous < 30_000) return false;
            if (lastReplies.Count >= 256)
            {
                foreach (var expired in lastReplies.Where(p => now - p.Value >= 30_000).Select(p => p.Key).ToList())
                    lastReplies.Remove(expired);
                if (lastReplies.Count >= 256) return false;
            }
            lastReplies[key] = now;
            return true;
        }
    }
}
