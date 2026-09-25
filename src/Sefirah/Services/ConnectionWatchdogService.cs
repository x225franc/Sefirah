using System.Net.NetworkInformation;
using Sefirah.Data.Models;
using Ping = Sefirah.Data.Models.Ping;

namespace Sefirah.Services;

/// <summary>
/// See <see cref="IConnectionWatchdogService"/>.
/// </summary>
public class ConnectionWatchdogService(
    ILogger<ConnectionWatchdogService> logger,
    IDeviceManager deviceManager,
    ISessionManager sessionManager,
    IDiscoveryService discoveryService) : IConnectionWatchdogService
{
    // Comfortably under typical router NAT/conntrack idle timeouts for TCP (commonly 60s+),
    // and frequent enough that a dead socket doesn't sit unnoticed for long.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    // Two missed heartbeats' worth of silence before we give up on a connection.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(50);

    // Windows tends to fire NetworkAddressChanged several times in quick succession during
    // a single network transition; collapse those into one drop+retry pass.
    private static readonly TimeSpan NetworkChangeDebounce = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? cts;
    private bool networkChangeSubscribed;
    private DateTime lastNetworkChangeHandledUtc = DateTime.MinValue;

    private ObservableCollection<PairedDevice> PairedDevices => deviceManager.PairedDevices;

    public void Start()
    {
        if (cts is not null) return;

        cts = new CancellationTokenSource();
        _ = RunAsync(cts.Token);

        try
        {
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
            networkChangeSubscribed = true;
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to subscribe to network address changes", ex);
        }
    }

    public void Stop()
    {
        if (networkChangeSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            networkChangeSubscribed = false;
        }

        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - lastNetworkChangeHandledUtc < NetworkChangeDebounce) return;
        lastNetworkChangeHandledUtc = now;

        logger.Info("Local network address changed; dropping stale connections and retrying");
        try
        {
            // A socket bound to an address/interface that just disappeared (Wi-Fi switch,
            // adapter reset, VPN toggle) rarely reports an error by itself - drop it so the
            // retry pass below can establish a fresh one.
            foreach (var device in PairedDevices.Where(d => d.IsConnected))
            {
                sessionManager.DisconnectDevice(device);
            }

            discoveryService.BroadcastNow();
            RetryDisconnectedDevices();
        }
        catch (Exception ex)
        {
            logger.Error("Error handling network address change", ex);
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Stop()
        }
    }

    private void Tick()
    {
        try
        {
            SendHeartbeatsAndPruneStale();
            RetryDisconnectedDevices();
        }
        catch (Exception ex)
        {
            logger.Error("Error in connection watchdog tick", ex);
        }
    }

    private void SendHeartbeatsAndPruneStale()
    {
        var now = DateTime.UtcNow;
        foreach (var device in PairedDevices.Where(d => d.IsConnected))
        {
            if (device.LastActivityUtc != default && now - device.LastActivityUtc > StaleThreshold)
            {
                logger.Warn($"No traffic from {device.Name} for {(now - device.LastActivityUtc).TotalSeconds:F0}s, treating connection as dead");
                sessionManager.DisconnectDevice(device);
                continue;
            }

            device.SendMessage(new Ping());
        }
    }

    /// <summary>Retries every paired device that is disconnected but not by the user's own choice.</summary>
    private void RetryDisconnectedDevices()
    {
        foreach (var device in PairedDevices.Where(d => !d.IsConnectedOrConnecting && !d.IsForcedDisconnect))
        {
            sessionManager.Connect(device);
        }
    }
}
