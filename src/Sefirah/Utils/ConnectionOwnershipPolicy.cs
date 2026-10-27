namespace Sefirah.Utils;

public static class ConnectionOwnershipPolicy
{
    public static bool CanAdopt(string localId, string remoteId, bool incoming, bool paused,
        bool userInitiated, bool hasIncoming, bool hasOutgoing, bool outgoingPending)
    {
        if (localId == remoteId || paused && !userInitiated) return false;
        if (incoming && hasIncoming || !incoming && hasOutgoing) return false;
        var preferOutgoing = string.CompareOrdinal(localId, remoteId) < 0;
        if (incoming && preferOutgoing && (hasOutgoing || outgoingPending)) return false;
        if (!incoming && !preferOutgoing && hasIncoming) return false;
        return true;
    }
}
