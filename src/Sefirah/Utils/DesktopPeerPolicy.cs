namespace Sefirah.Utils;

public static class DesktopPeerPolicy
{
    public static bool IsDesktop(string? type) =>
        string.Equals(type, "windows", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "desktop", StringComparison.OrdinalIgnoreCase);

    // Legacy Android peers do not announce a type and retain bidirectional discovery.
    public static bool ShouldInitiate(string localId, string remoteId, string? remoteType) =>
        localId != remoteId && (!IsDesktop(remoteType) || string.CompareOrdinal(localId, remoteId) < 0);
}
