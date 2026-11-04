using System.Net;
using System.Text;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Media.Imaging;
using Sefirah.Data.AppDatabase.Models;
using Sefirah.Data.EventArguments;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;

namespace Sefirah.Services;

public class DiscoveryService(
    ILogger logger,
    IMdnsService mdnsService,
    IDeviceManager deviceManager,
    ISessionManager sessionManager
    ) : IDiscoveryService, IUdpClientProvider
{
    private MulticastClient? udpClient;
    private const string DEFAULT_BROADCAST = "255.255.255.255";
    private LocalDeviceEntity? localDevice;
    private readonly int port = 5149;
    private List<IPEndPoint> broadcastEndpoints = [];
    private const int DiscoveryPort = 5149;
    private static readonly TimeSpan RebroadcastInterval = TimeSpan.FromSeconds(60);
    private CancellationTokenSource? rebroadcastCts;
    private readonly DiscoveryReplyGate replyGate = new();

    public UdpBroadcast? BroadcastMessage { get; private set; }

    public async Task StartDiscoveryAsync()
    {
        try
        {
            if (NetworkService.ServerPort is < 1 or > 65535)
            {
                logger.Warn("Discovery is unavailable because the connection server did not start");
                return;
            }
            localDevice = await deviceManager.GetLocalDeviceAsync();

            BroadcastMessage = new UdpBroadcast
            {
                DeviceId = localDevice.DeviceId,
                DeviceName = localDevice.DeviceName,
                DeviceType = "windows",
                Port = NetworkService.ServerPort
            };

            mdnsService.AdvertiseService(BroadcastMessage, port);
            mdnsService.StartDiscovery();
            mdnsService.DiscoveredMdnsService += OnDiscoveredMdnsService;

            RefreshBroadcastEndpoints();

            udpClient = new MulticastClient("0.0.0.0", port, this, logger)
            {
                OptionDualMode = false,
                OptionMulticast = true,
                OptionReuseAddress = true,
            };
            udpClient.SetupMulticast(true);

            if (udpClient.Connect())
            {
                udpClient.Socket.EnableBroadcast = true;
                logger.Info($"UDP Client connected successfully {port}");
                BroadcastDeviceInfoAsync(BroadcastMessage);
                StartPeriodicRebroadcast();
            }
            else
            {
                logger.Error("Failed to connect UDP client");
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Discovery initialization failed: {ex.Message}", ex);
        }
    }

    private void OnDiscoveredMdnsService(object? sender, DiscoveredMdnsServiceArgs e)
    {
        sessionManager.Connect(e.DeviceId, e.Address, e.Port);
    }

    /// <summary>
    /// Re-announces this device on the network right away. Called by
    /// <see cref="ConnectionWatchdogService"/> after a network change, since the phone
    /// may have missed the one-time broadcast sent when discovery first started.
    /// </summary>
    public void BroadcastNow()
    {
        if (BroadcastMessage is not null)
        {
            // DHCP/Wi-Fi may have changed since startup; don't keep sending to the old subnet.
            RefreshBroadcastEndpoints();
            BroadcastDeviceInfoAsync(BroadcastMessage);
        }
    }

    private void RefreshBroadcastEndpoints()
    {
        var endpoints = NetworkHelper.GetAllValidAddresses().Select(ipInfo =>
        {
            var network = new Data.Models.IPNetwork(ipInfo.Address, ipInfo.SubnetMask);
            var broadcast = network.BroadcastAddress;
            return new IPEndPoint(broadcast.Equals(IPAddress.Broadcast) && ipInfo.Gateway is not null
                ? ipInfo.Gateway : broadcast, DiscoveryPort);
        }).ToList();
        endpoints.Add(new IPEndPoint(IPAddress.Parse(DEFAULT_BROADCAST), DiscoveryPort));
        endpoints.AddRange(deviceManager.GetRemoteDeviceAddresses()
            .Select(address => IPAddress.TryParse(address, out var parsed) ? new IPEndPoint(parsed, DiscoveryPort) : null)
            .OfType<IPEndPoint>());
        var updated = endpoints.Distinct().ToList();
        if (!broadcastEndpoints.SequenceEqual(updated))
            logger.Info($"Active broadcast endpoints: {string.Join(", ", updated)}");
        broadcastEndpoints = updated;
    }

    private void StartPeriodicRebroadcast()
    {
        rebroadcastCts?.Cancel();
        rebroadcastCts = new CancellationTokenSource();
        var token = rebroadcastCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(RebroadcastInterval);
                while (await timer.WaitForNextTickAsync(token))
                {
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        if (deviceManager.PairedDevices.Any(d => !d.IsConnectedOrConnecting && !d.IsForcedDisconnect))
                            BroadcastNow();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // expected on stop
            }
        }, token);
    }

    private async void BroadcastDeviceInfoAsync(UdpBroadcast udpBroadcast)
    {
        if (udpClient is null || udpBroadcast is null) return;
        
        string jsonMessage = JsonMessageSerializer.Serialize(udpBroadcast);
        byte[] messageBytes = Encoding.UTF8.GetBytes(jsonMessage);
        foreach (var endPoint in broadcastEndpoints)
        {
            try
            {
                udpClient.Socket.SendTo(messageBytes, endPoint);
            }
            catch
            {
                // ignore
            }
        }
    }

    public async void OnReceived(EndPoint endpoint, byte[] buffer, long offset, long size)
    {
        try
        {
            var message = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
            var address = ((IPEndPoint)endpoint).Address;
            if (JsonMessageSerializer.DeserializeMessage(message) is not UdpBroadcast broadcast) return;

            if (string.IsNullOrWhiteSpace(broadcast.DeviceId) || broadcast.Port is < 1 or > 65535 ||
                broadcast.DeviceId == localDevice?.DeviceId || address is null || localDevice is null) return;

            if (BroadcastMessage is not null && udpClient is not null &&
                replyGate.TryReply(localDevice.DeviceId, broadcast.DeviceId, broadcast.DeviceType,
                    address.ToString(), Environment.TickCount64))
            {
                var reply = Encoding.UTF8.GetBytes(JsonMessageSerializer.Serialize(BroadcastMessage));
                udpClient.Socket.SendTo(reply, new IPEndPoint(address, DiscoveryPort));
            }

            if (!DesktopPeerPolicy.ShouldInitiate(localDevice!.DeviceId, broadcast.DeviceId, broadcast.DeviceType)) return;
            sessionManager.Connect(broadcast.DeviceId, address.ToString(), broadcast.Port);
        }
        catch (Exception ex)
        {
            logger.Warn($"Error processing UDP message: {ex.Message}", ex);
        }
    }

    public void StopDiscovery()
    {
        try
        {
            rebroadcastCts?.Cancel();
            rebroadcastCts?.Dispose();
            rebroadcastCts = null;

            mdnsService.DiscoveredMdnsService -= OnDiscoveredMdnsService;
            mdnsService.UnAdvertiseService();
            udpClient?.Dispose();
            udpClient = null;
        }
        catch (Exception ex)
        {
            logger.Error($"Error disposing default UDP client: {ex.Message}", ex);
        }
    }

    public async Task<BitmapImage?> GenerateQrCodeAsync()
    {
        try
        {
            var broadcast = BroadcastMessage;
            if (broadcast is null)
            {
                return null;
            }

            var localAddresses = NetworkHelper.GetAllValidAddresses();
            var addresses = localAddresses.Select(addr => addr.Address.ToString()).ToList();

            var payload = new QrCodePayload
            {
                Addresses = addresses,
                Port = broadcast.Port,
                DeviceId = broadcast.DeviceId,
                DeviceName = broadcast.DeviceName
            };
            var json = JsonMessageSerializer.Serialize(payload);
            var deepLink = $"sefirah://pair?data={Uri.EscapeDataString(json)}";

            var qrCodeBytes = ImageHelper.GenerateQrCode(deepLink);
            if (qrCodeBytes is null)
            {
                return null;
            }

            return await qrCodeBytes.ToBitmapAsync(256);
        }
        catch (Exception ex)
        {
            logger.Warn($"Error generating QR code: {ex.Message}", ex);
            return null;
        }
    }

}
