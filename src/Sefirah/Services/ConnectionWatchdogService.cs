using System.Net.NetworkInformation;
using CommunityToolkit.WinUI;
using Microsoft.Win32;
using Sefirah.Data.Models;
using Sefirah.Utils;
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
    // One local timer; network probes are sent only after an idle period.
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    // Windows tends to fire NetworkAddressChanged several times in quick succession during
    // a single network transition; collapse those into one drop+retry pass.
    private static readonly TimeSpan NetworkChangeDebounce = TimeSpan.FromSeconds(3);

    private CancellationTokenSource? cts;
    private bool networkChangeSubscribed;
    private bool powerChangeSubscribed;
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
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
            if (OperatingSystem.IsWindows())
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                powerChangeSubscribed = true;
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to subscribe to network address changes", ex);
        }
    }

    public void Stop()
    {
        if (powerChangeSubscribed && OperatingSystem.IsWindows())
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            powerChangeSubscribed = false;
        }
        if (networkChangeSubscribed)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            networkChangeSubscribed = false;
        }

        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                _ = PrepareForPowerTransitionAsync();
                break;
            case PowerModes.Resume:
                _ = RecoverConnectionsAsync("resume from sleep");
                break;
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - lastNetworkChangeHandledUtc < NetworkChangeDebounce) return;
        lastNetworkChangeHandledUtc = now;

        _ = RecoverConnectionsAsync("network address changed");
    }

    private async Task RecoverConnectionsAsync(string reason)
    {
        var token = cts?.Token;
        if (token is null || !await recoveryGate.WaitAsync(0)) return;
        try
        {
            logger.Info($"Recovering connections after {reason}");
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                foreach (var device in PairedDevices.Where(d => d.IsConnected).ToList())
                    sessionManager.DisconnectDevice(device, false);
            });
            // Allow close callbacks and the resumed network adapter to settle.
            await Task.Delay(TimeSpan.FromSeconds(2), token.Value);
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (token.Value.IsCancellationRequested) return;
                sessionManager.ResetConnectionRetries();
                discoveryService.BroadcastNow();
                RetryDisconnectedDevices();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.Error("Error recovering connections", ex);
        }
        finally { recoveryGate.Release(); }
    }

    private async Task PrepareForPowerTransitionAsync()
    {
        var token = cts?.Token;
        if (token is null || !await recoveryGate.WaitAsync(0)) return;
        try
        {
            logger.Info("Closing device connections before sleep or hibernation");
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                foreach (var device in PairedDevices.Where(d => d.IsConnectedOrConnecting).ToList())
                    sessionManager.DisconnectDevice(device, false);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.Error("Error preparing connections for power transition", ex);
        }
        finally { recoveryGate.Release(); }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(token))
            {
                await App.MainWindow.DispatcherQueue.EnqueueAsync(Tick);
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
        var now = Environment.TickCount64;
        foreach (var device in PairedDevices.Where(d => d.IsConnected).ToList())
        {
            var action = device.Liveness.Check(now);
            if (action == LivenessAction.Disconnect)
            {
                logger.Warn($"No response to the idle probe from {device.Name}, treating connection as dead");
                sessionManager.DisconnectDevice(device);
                continue;
            }

            if (action == LivenessAction.Ping) device.SendMessage(new Ping());
        }
    }

    /// <summary>Retries every paired device that is disconnected but not by the user's own choice.</summary>
    private void RetryDisconnectedDevices()
    {
        foreach (var device in PairedDevices.Where(d => !d.IsConnectedOrConnecting && !d.IsForcedDisconnect))
        {
            sessionManager.RetryConnection(device);
        }
    }
}
