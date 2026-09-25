namespace Sefirah.Data.Contracts;

/// <summary>
/// Keeps paired devices connected without requiring the user to manually reconnect.
/// Mirrors the Android app's ConnectionWatchdog: sends periodic heartbeats to detect
/// sockets that die silently, retries disconnected paired devices, and reacts to local
/// network changes (Wi-Fi switch, adapter up/down).
/// </summary>
public interface IConnectionWatchdogService
{
    void Start();

    void Stop();
}
