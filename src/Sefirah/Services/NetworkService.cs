using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CommunityToolkit.WinUI;
using Sefirah.Data.Models;
using Sefirah.Dialogs;
using Sefirah.Helpers;
using Sefirah.Services.Socket;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;

namespace Sefirah.Services;

public class NetworkService(
    Lazy<IMessageHandler> messageHandler,
    ILogger<NetworkService> logger,
    IDeviceManager deviceManager,
    IAdbService adbService) : INetworkService, ISessionManager, ITcpServerProvider, ITcpClientProvider
{
    public static int ServerPort { get; private set; }

    private Server? server;
    private bool isRunning;

    private static readonly IEnumerable<int> PORT_RANGE = Enumerable.Range(5150, 20); // 5150 to 5169

    private readonly ConcurrentDictionary<Guid, Sefirah.Utils.WireMessageBuffer> connectionBuffers = [];
    private readonly ConcurrentDictionary<Guid, DeviceAuth> deviceAuths = [];
    private readonly HashSet<string> connectingDeviceIds = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> handshakeCompletion = [];
    private readonly ConcurrentDictionary<string, CancellationTokenSource> connectionCancellationTokens = [];
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> authenticationCompletion = [];
    private readonly ConcurrentDictionary<Guid, string> expectedClientDevices = [];
    private readonly ConcurrentDictionary<string, byte> pausedDevices = [];
    private readonly ConnectionRetrySchedule retrySchedule = new();

    private sealed class DeviceAuth
    {
        public ConcurrentQueue<SocketMessage> Deferred { get; } = new();
        public CancellationTokenSource Cts { get; } = new();
    }

    private ObservableCollection<PairedDevice> PairedDevices => deviceManager.PairedDevices;
    private ObservableCollection<DiscoveredDevice> DiscoveredDevices => deviceManager.DiscoveredDevices;

    /// <summary>
    /// Event fired when a device connection status changes
    /// </summary>
    public event EventHandler<PairedDevice>? ConnectionStatusChanged;

    public async Task StartServerAsync()
    {
        if (isRunning) return;

        ConnectionStatusChanged += ConnectionStatusChangedEvent;

        foreach (int port in PORT_RANGE)
        {
            try
            {
                server = new Server(SslHelper.GetSslContext(), IPAddress.IPv6Any, port, this)
                {
                    OptionReuseAddress = true,
                    OptionDualMode = true,
                };

                if (server.Start())
                {
                    ServerPort = port;
                    isRunning = true;
                    logger.Info($"Server started on port: {port}");
                    return;
                }
                server.Dispose();
                server = null;
            }
            catch (Exception ex)
            {
                logger.Error($"Error starting server on port {port}", ex);
                server?.Dispose();
                server = null;
            }
        }
        logger.Error($"Failed to start server");
    }

    private async void ConnectionStatusChangedEvent(object? sender, PairedDevice device)
    {
        if (device.IsConnected)
        {
            device.LastActivityUtc = DateTime.UtcNow;
            device.Liveness.Received(Environment.TickCount64);

            await SendDeviceInfo(device);

            if (device.IsPhone && device.DeviceSettings.AdbAutoConnect)
            {
                await adbService.TryConnectTcp(device.Address, device.Model);
            }
        }
    }

    private async Task SendDeviceInfo(PairedDevice device)
    {
        try
        {
            var localDevice = await deviceManager.GetLocalDeviceAsync();
            var avatar = await UserInformation.GetCurrentUserAvatarAsync();
            device.SendMessage(new DeviceInfo { DeviceName = localDevice.DeviceName, Avatar = avatar, DeviceType = "windows" });
        }
        catch (Exception ex)
        {
            logger.Error($"Exception occurred while sending device info", ex);
        }
    }

    public void DisconnectDevice(PairedDevice device, bool forcedDisconnect = false)
    {
        DisconnectDeviceCore(device, forcedDisconnect, notifyPeer: forcedDisconnect);
    }

    private void DisconnectDeviceCore(PairedDevice device, bool forcedDisconnect, bool notifyPeer)
    {
        // Set the pause before cancelling attempts or closing sockets: late discovery/auth must
        // never undo the button's action while the close callbacks are still queued.
        if (forcedDisconnect) pausedDevices[device.Id] = 0;
        if (connectionCancellationTokens.TryGetValue(device.Id, out var attempt)) attempt.Cancel();
        App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            var session = device.Session;
            var client = device.Client;
            device.Session = null;
            device.Client = null;
            PublishDisconnected(device, forcedDisconnect);
            _ = CloseDeviceTransportsAsync(session, client, notifyPeer);
        });
    }

    private async Task CloseDeviceTransportsAsync(ServerSession? session, Client? client, bool notifyPeer)
    {
        if (notifyPeer)
        {
            // Send() writes the tiny final frame before closing. Bound the wait: a broken
            // peer must not block the UI or prevent local disconnection.
            try
            {
                var bytes = EncodeMessage(new Disconnect());
                await Task.Run(() =>
                {
                    if (session is not null) session.Send(bytes);
                    if (client is not null) client.Send(bytes);
                }).WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch (Exception ex) { logger.Debug("Could not flush disconnect message", ex); }
        }
        if (session is not null) DisconnectSession(session);
        if (client is not null) DisconnectClient(client);
    }

    public void SendMessage(ServerSession session, SocketMessage message)
    {
        try
        {
            var bytes = EncodeMessage(message);
            session.SendAsync(bytes);
        }
        catch (Exception ex)
        {
            logger.Error($"Error sending message", ex);
        }
    }

    public void SendMessage(Client client, SocketMessage message)
    {
        try
        {
            var bytes = EncodeMessage(message);
            client.SendAsync(bytes);
        }
        catch (Exception ex)
        {
            logger.Error($"Error sending message to client", ex);
        }
    }

    public void BroadcastMessage(SocketMessage message)
    {
        if (PairedDevices.Count == 0) return;

        foreach (var device in PairedDevices.Where(d => d.IsConnected))
        {
            device.SendMessage(message);
        }
    }

    private static byte[] EncodeMessage(SocketMessage message) =>
        Encoding.UTF8.GetBytes(JsonMessageSerializer.Serialize(message) + "\n");

    /// <summary>
    /// Extracts complete newline-delimited messages from the buffer, deserializes them, removes from buffer, returns list.
    /// </summary>
    private static List<SocketMessage> GetMessagesFromBuffer(Sefirah.Utils.WireMessageBuffer sb, ILogger logger)
    {
        List<SocketMessage> messages = [];
        foreach (var messageString in sb.ReadMessages())
        {
            if (string.IsNullOrEmpty(messageString)) continue;

            logger.Debug($"Processing message: {(messageString.Length > 100 ? string.Concat(messageString.AsSpan(0, 100), "...") : messageString)}");

            var socketMessage = JsonMessageSerializer.DeserializeMessage(messageString);
            if (socketMessage is not null)
            {
                messages.Add(socketMessage);
            }
        }
        return messages;
    }

    public async Task SendAuthenticationMessage(Action<SocketMessage> send, bool userInitiated = false)
    {
        var localDevice = await deviceManager.GetLocalDeviceAsync();

        var authResponse = new Authentication
        {
            DeviceId = localDevice.DeviceId,
            DeviceName = localDevice.DeviceName,
            PublicKey = SslHelper.DevicePublicKeyString,
            UserInitiated = userInitiated,
            DeviceType = "windows",
            Port = ServerPort,
            Model = Environment.MachineName
        };

        send(authResponse);
    }

    #region Server events
    public void OnConnected(ServerSession session) 
    {
        logger.Debug($"New session connected: {session.Id}");
        connectionBuffers[session.Id] = new Sefirah.Utils.WireMessageBuffer();
    }

    public void OnDisconnected(ServerSession session)
    {
        logger.Debug($"Session disconnected: {session.Id}");
        if (!connectionBuffers.ContainsKey(session.Id)) return;

        DisconnectSession(session);
    }

    public void OnError(SocketError error)
    {
        logger.Error($"Error on socket {error}");
    }

    public void OnReceived(ServerSession session, byte[] buffer, long offset, long size)
    {
        try
        {
            if (!connectionBuffers.TryGetValue(session.Id, out var sb)) return;
            sb.Append(buffer, (int)offset, (int)size);

            foreach (var socketMessage in GetMessagesFromBuffer(sb, logger))
            {
                if (socketMessage is Authentication authMessage)
                {
                    var deviceAuth = StartAuth(session.Id);
                    AuthenticateSessionAsync(session, authMessage, deviceAuth);
                    continue;
                }

                if (deviceAuths.TryGetValue(session.Id, out var active))
                {
                    active.Deferred.Enqueue(socketMessage);
                    continue;
                }

                RouteMessage(session.Id, socketMessage);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error in OnReceived for session {session.Id}", ex);
        }
    }

    private DeviceAuth StartAuth(Guid connectionId)
    {
        var deviceAuth = new DeviceAuth();
        if (deviceAuths.TryRemove(connectionId, out var previous))
        {
            previous.Cts.Cancel();
            previous.Cts.Dispose();
        }
        deviceAuths[connectionId] = deviceAuth;
        return deviceAuth;
    }

    private async void AuthenticateSessionAsync(ServerSession session, Authentication authMessage, DeviceAuth deviceAuth)
    {
        try
        {
            await HandleServerSessionAuthentication(session, authMessage, deviceAuth.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.Debug($"Auth cancelled for session {session.Id}");
        }
        finally
        {
            FinalizeAuth(session.Id, deviceAuth);
        }
    }

    private void FinalizeAuth(Guid connectionId, DeviceAuth deviceAuth)
    {
        if (!deviceAuths.TryRemove(KeyValuePair.Create(connectionId, deviceAuth)))
            return;

        deviceAuth.Cts.Dispose();
        while (deviceAuth.Deferred.TryDequeue(out var message))
            RouteMessage(connectionId, message);
    }

    private void CancelAuth(Guid connectionId)
    {
        if (!deviceAuths.TryRemove(connectionId, out var deviceAuth))
            return;

        deviceAuth.Cts.Cancel();
        deviceAuth.Cts.Dispose();
    }

    /// <summary>
    /// Routes a deserialized message from a client and server to the appropriate handler
    /// </summary>
    private void RouteMessage(Guid guid, SocketMessage message)
    {
        App.MainWindow.DispatcherQueue.EnqueueAsync(() => RouteMessageOnDispatcher(guid, message));
    }

    private void RouteMessageOnDispatcher(Guid guid, SocketMessage message)
    {
        var pairedDevice = PairedDevices.FirstOrDefault(d => (d.Client?.Id == guid || d.Session?.Id == guid));
        if (pairedDevice is not null)
        {
            pairedDevice.LastActivityUtc = DateTime.UtcNow;
            pairedDevice.Liveness.Received(Environment.TickCount64);

            if (message is Disconnect)
            {
                DisconnectDeviceCore(pairedDevice, forcedDisconnect: true, notifyPeer: false);
                return;
            }

            if (message is Ping)
            {
                pairedDevice.SendMessage(new Pong());
                return;
            }
            if (message is Pong)
            {
                return;
            }

            messageHandler.Value.HandleMessageAsync(pairedDevice, message);
            return;
        }

        var discoveredDevice = DiscoveredDevices.FirstOrDefault(d => d.Client?.Id == guid || d.Session?.Id == guid);
        if (discoveredDevice is not null && message is PairMessage pairMessage)
        {
            HandlePairMessage(discoveredDevice, pairMessage);
            return;
        }

        logger.Warn($"Received message from unknown client");
    }

    private void HandlePairMessage(DiscoveredDevice device, PairMessage pairMessage)
    {
        if (device.IsPairing)
            HandlePairResponse(device, pairMessage);
        else if (pairMessage.Pair)
            HandlePairRequest(device);
    }

    #endregion

    #region Server Authentication

    private async Task HandleServerSessionAuthentication(ServerSession session, Authentication authMessage, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (session.Socket.RemoteEndPoint is not IPEndPoint endPoint) return;

            var ip = endPoint.Address;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            var address = ip.ToString();
            logger.Info($"Received connection from {address}");

            // Server-side cert-based auth: connecting client sends PublicKey; we look up the stashed cert
            var cert = SslHelper.GetCertForPublicKey(authMessage.PublicKey);
            if (cert is null || cert.Length == 0)
            {
                logger.Warn($"No client certificate or PublicKey mismatch; rejecting connection");
                throw new Exception("Client certificate required");
            }


            var localDevice = await deviceManager.GetLocalDeviceAsync();
            if (authMessage.DeviceId == localDevice.DeviceId)
                throw new InvalidOperationException("Cannot connect to this device itself");

            var pairedDevice = PairedDevices.FirstOrDefault(d => d.Id == authMessage.DeviceId);
            if (pairedDevice is not null)
            {
                if (pairedDevice.Certificate.Length == 0 || cert.Length != pairedDevice.Certificate.Length || !cert.AsSpan().SequenceEqual(pairedDevice.Certificate))
                {
                    throw new Exception("Certificate verification failed for paired device");
                }
                pairedDevice.DeviceType = authMessage.DeviceType ?? pairedDevice.DeviceType;
                if (authMessage.Port > 0) pairedDevice.Port = authMessage.Port;
                var accepted = await AdoptConnection(pairedDevice, session, null, address,
                    localDevice.DeviceId, authMessage.UserInitiated, cancellationToken);
                if (!accepted) { DisconnectSession(session); return; }
                await SendAuthenticationMessage(m => SendMessage(session, m));
                ConnectionStatusChanged?.Invoke(this, pairedDevice);
                await deviceManager.UpdateDevice(pairedDevice);
                return;
            }

            await SendAuthenticationMessage(m => SendMessage(session, m));
            await AddDiscoveredDevice(session, authMessage, address, cert);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error($"Error in session authentication", ex);
            DisconnectSession(session);
        }
    }

    private async Task<bool> AdoptConnection(PairedDevice device, ServerSession? session, Client? client,
        string address, string localId, bool userInitiated, CancellationToken cancellationToken)
    {
        bool accepted = false;
        ServerSession? oldSession = null;
        Client? oldClient = null;
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session is not null && !connectionBuffers.ContainsKey(session.Id) ||
                client is not null && !connectionBuffers.ContainsKey(client.Id)) return;
            if (client is not null && expectedClientDevices.TryGetValue(client.Id, out var expected) && expected != device.Id)
                return;

            var incoming = session is not null;
            // Both peers choose the same socket during simultaneous opens. Keep healthy same-
            // direction sessions, so repeated advertisements cannot replace a working transport.
            var outgoingPending = connectionCancellationTokens.TryGetValue(device.Id, out var currentAttempt) && !currentAttempt.IsCancellationRequested;
            if (!ConnectionOwnershipPolicy.CanAdopt(localId, device.Id, incoming,
                pausedDevices.ContainsKey(device.Id) || device.IsForcedDisconnect, userInitiated,
                device.Session is not null, device.Client is not null, outgoingPending)) return;

            oldSession = device.Session;
            oldClient = device.Client;
            device.Session = session;
            device.Client = client;
            pausedDevices.TryRemove(device.Id, out _);
            retrySchedule.Reset(device.Id);
            device.Address = address;
            device.TryAddAddress(address);
            device.LastActivityUtc = DateTime.UtcNow;
            device.Liveness.Received(Environment.TickCount64);
            device.ConnectionStatus = new Connected();
            deviceManager.ActiveDevice ??= device;
            if (incoming && connectionCancellationTokens.TryGetValue(device.Id, out var pending)) pending.Cancel();
            accepted = true;
        });
        if (oldSession is not null) DisconnectSession(oldSession);
        if (oldClient is not null) DisconnectClient(oldClient);
        return accepted;
    }

    private async Task AddDiscoveredDevice(ServerSession session, Authentication authMessage, string address, byte[] certificate)
    {
        var existingDevice = DiscoveredDevices.FirstOrDefault(d => d.Id == authMessage.DeviceId);
        if (existingDevice is not null && existingDevice.Session is not null)
        {
            DisconnectSession(existingDevice.Session);
        }

        var device = new DiscoveredDevice
        {
            Id = authMessage.DeviceId,
            Name = authMessage.DeviceName,
            Model = authMessage.Model,
            DeviceType = authMessage.DeviceType,
            Address = address,
            Port = authMessage.Port > 0 ? authMessage.Port : 5150,
            Certificate = certificate,
            VerificationKey = SslHelper.GetVerificationCode(authMessage.PublicKey),
            Session = session
        };

        await App.MainWindow.DispatcherQueue.EnqueueAsync(() => DiscoveredDevices.Add(device));

    }

    private async void HandlePairRequest(DiscoveredDevice device)
    {
        logger.Info($"Received pairing request from {device.Name}");

        var tcs = new TaskCompletionSource<bool>();
        await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            try
            {
                var frame = (Frame)App.MainWindow.Content!;
                var dialog = new ConnectionRequestDialog(device.Name, device.VerificationKey, frame)
                {
                    XamlRoot = App.MainWindow.Content!.XamlRoot
                };

                var result = await dialog.ShowAsync();
                tcs.SetResult(result is ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                logger.Error($"Error showing connection request dialog", ex);
                tcs.SetResult(false);
            }
        });

        var accepted = await tcs.Task;

        device.SendMessage(new PairMessage { Pair = accepted });
        if (accepted)
        {
            var pairedDevice = await deviceManager.AddDevice(device);
            ConnectionStatusChanged?.Invoke(this, pairedDevice);
        }
    }

    private async void HandlePairResponse(DiscoveredDevice device, PairMessage pairMessage)
    {
        if (!pairMessage.Pair)
        {
            logger.Info($"Device {device.Name} rejected pairing request");
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() => device.IsPairing = false);
            return;
        }

        logger.Info($"Device {device.Name} accepted pairing request");
        var pairedDevice = await deviceManager.AddDevice(device);
        ConnectionStatusChanged?.Invoke(this, pairedDevice);
    }
    #endregion

    public void DisconnectSession(ServerSession session, bool forcedDisconnect = false)
    {
        logger.Debug($"disconnecing session: {session.Id}");
        try
        {
            connectionBuffers.TryRemove(session.Id, out _);
            CancelAuth(session.Id);
            App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                // Test ownership and clear it in the same dispatcher turn as adoption.
                // A delayed close callback can never clear a newer socket.
                var pairedDevice = PairedDevices.FirstOrDefault(d => ReferenceEquals(d.Session, session));
                if (pairedDevice is not null)
                {
                    pairedDevice.Session = null;
                    if (pairedDevice.Client is null) PublishDisconnected(pairedDevice, forcedDisconnect);
                }
                var discoveredDevice = DiscoveredDevices.FirstOrDefault(d => ReferenceEquals(d.Session, session));
                if (discoveredDevice is not null) DiscoveredDevices.Remove(discoveredDevice);
            });
            session.Disconnect();
            session.Dispose();
        }
        catch (Exception ex)
        {
            logger.Error($"Error in Disconnecting", ex);
        }
    }

    public void DisconnectClient(Client client, bool forcedDisconnect = false)
    {
        try
        {
            logger.Debug($"disconnecing client session: {client.Id}");
            connectionBuffers.TryRemove(client.Id, out _);
            CancelAuth(client.Id);
            if (authenticationCompletion.TryRemove(client.Id, out var completion)) completion.TrySetResult(false);
            if (handshakeCompletion.TryRemove(client.Id, out var handshake)) handshake.TrySetResult(false);

            App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                var device = PairedDevices.FirstOrDefault(d => ReferenceEquals(d.Client, client));
                if (device is not null)
                {
                    device.Client = null;
                    if (device.Session is null) PublishDisconnected(device, forcedDisconnect);
                }
                var discoveredDevice = DiscoveredDevices.FirstOrDefault(d => ReferenceEquals(d.Client, client));
                if (discoveredDevice is not null) DiscoveredDevices.Remove(discoveredDevice);
            });
            client.Disconnect();
            client.Dispose();
        }
        catch (Exception ex)
        {
            logger.Error($"Error disconnecting client", ex);
        }
    }

    private void PublishDisconnected(PairedDevice device, bool forcedDisconnect)
    {
        device.ConnectionStatus = new Disconnected(forcedDisconnect || pausedDevices.ContainsKey(device.Id));
        ConnectionStatusChanged?.Invoke(this, device);
    }

    #region Client

    public async void Connect(string deviceId, string address, int port)
    {
        var existingDevice = PairedDevices.FirstOrDefault(d => d.Id == deviceId);
        if (existingDevice is not null)
        {
            if (existingDevice.IsConnectedOrConnecting || existingDevice.IsForcedDisconnect || pausedDevices.ContainsKey(deviceId))
                return;

            existingDevice.Port = port;
            ConnectCore(existingDevice, [address], userInitiated: false);
            return;
        }

        if (DiscoveredDevices.Any(d => d.Id == deviceId)) return;

        lock (connectingDeviceIds)
        {
            if (connectingDeviceIds.Contains(deviceId)) return;
            connectingDeviceIds.Add(deviceId);
        }

        Client? client = null;
        try
        {
            logger.Info($"Connecting to {address}:{port}");

            client = new Client(SslHelper.GetSslContext(), address, port, this);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            handshakeCompletion[client.Id] = tcs;

            try
            {
                if (client.ConnectAsync())
                {
                    if (!client.IsHandshaked)
                        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    await SendAuthenticationMessage(m => SendMessage(client, m));
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Error connecting to {address}:{port}", ex);
                DisconnectClient(client);
            }
        }
        finally
        {
            if (client is not null)
                handshakeCompletion.TryRemove(client.Id, out _);

            lock (connectingDeviceIds)
            {
                connectingDeviceIds.Remove(deviceId);
            }
        }
    }

    public void Connect(PairedDevice device)
    {
        if (connectionCancellationTokens.TryGetValue(device.Id, out _))
        {
            DisconnectDevice(device, true);
            return;
        }
        if (device.IsConnected) return;
        pausedDevices.TryRemove(device.Id, out _);
        retrySchedule.Reset(device.Id);
        ConnectCore(device, device.GetEnabledAddresses(), userInitiated: true);
    }

    public void Connect(PairedDevice device, string address)
    {
        if (device.IsConnectedOrConnecting || connectionCancellationTokens.ContainsKey(device.Id)) return;
        pausedDevices.TryRemove(device.Id, out _);
        retrySchedule.Reset(device.Id);
        ConnectCore(device, [address], userInitiated: true);
    }

    public void RetryConnection(PairedDevice device) => ConnectCore(device, device.GetEnabledAddresses(), userInitiated: false);

    public void ResetConnectionRetries() => retrySchedule.ResetAll();

    private async void ConnectCore(PairedDevice device, IReadOnlyList<string> addresses, bool userInitiated)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        if (!connectionCancellationTokens.TryAdd(device.Id, cts)) { cts.Dispose(); return; }
        try
        {
            var proceed = false;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (token.IsCancellationRequested || device.IsConnected ||
                    !userInitiated && (device.IsForcedDisconnect || pausedDevices.ContainsKey(device.Id) || !retrySchedule.IsDue(device.Id, Environment.TickCount64))) return;
                device.ConnectionStatus = new Connecting();
                proceed = true;
            });
            if (!proceed) return;
            logger.Info($"Connecting to paired device {device.Name}");
            foreach (var address in addresses)
            {
                token.ThrowIfCancellationRequested();

                var clientContext = SslHelper.CreateSslContext(device.Certificate);

                logger.Info($"Connecting to {address}:{device.Port}");
                var client = new Client(clientContext, address, device.Port, this);
                var handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var authenticated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                handshakeCompletion[client.Id] = handshake;
                authenticationCompletion[client.Id] = authenticated;
                expectedClientDevices[client.Id] = device.Id;
                var adopted = false;

                try
                {
                    if (!client.ConnectAsync())
                    {
                        continue;
                    }

                    if (!client.IsHandshaked)
                    {
                        if (!await handshake.Task.WaitAsync(TimeSpan.FromSeconds(10), token)) continue;
                    }

                    token.ThrowIfCancellationRequested();
                    await SendAuthenticationMessage(m => SendMessage(client, m), userInitiated);
                    // TLS success alone is not a usable connection. Retain single-flight ownership
                    // until the peer identifies itself and the authenticated socket is adopted.
                    adopted = await authenticated.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                    if (adopted) return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Debug($"Failed to connect to {address}:{device.Port}", ex);
                }
                finally
                {
                    handshakeCompletion.TryRemove(client.Id, out _);
                    authenticationCompletion.TryRemove(client.Id, out _);
                    expectedClientDevices.TryRemove(client.Id, out _);
                    if (!adopted) DisconnectClient(client);
                }
            }
            logger.Warn($"Failed to connect to device {device.Name} on any IP address/port combination");
            retrySchedule.Failed(device.Id, Environment.TickCount64);
        }
        catch (OperationCanceledException)
        {
            logger.Info($"Connection attempt cancelled for device {device.Name}");
        }
        catch (Exception ex)
        {
            logger.Warn($"Connection attempt failed for device {device.Name}", ex);
            retrySchedule.Failed(device.Id, Environment.TickCount64);
        }
        finally
        {
            if (connectionCancellationTokens.TryRemove(KeyValuePair.Create(device.Id, cts)))
            {
                await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                {
                    if (device.IsConnecting && device.Session is null && device.Client is null && !connectionCancellationTokens.ContainsKey(device.Id))
                        device.ConnectionStatus = new Disconnected(pausedDevices.ContainsKey(device.Id));
                });
            }
            cts.Dispose();
        }
    }

    public void Pair(DiscoveredDevice device)
    {
        // Set this before sending: a fast desktop peer may respond immediately.
        device.IsPairing = true;
        device.SendMessage(new PairMessage { Pair = true });
    }

    #region Client events
    public void OnConnected(Client client)
    {
        logger.Info($"OnConnected for client {client.Id}");
        connectionBuffers[client.Id] = new Sefirah.Utils.WireMessageBuffer();
    }

    public void OnHandshaked(Client client)
    {
        if (handshakeCompletion.TryGetValue(client.Id, out var tcs))
            tcs.TrySetResult(true);
    }

    public void OnDisconnected(Client client)
    {
        if (handshakeCompletion.TryRemove(client.Id, out var tcs))
            tcs.TrySetException(new IOException("Disconnected before TLS handshake completed"));

        if (!connectionBuffers.ContainsKey(client.Id)) return;
        
        DisconnectClient(client);
    }

    public void OnError(Client client, SocketError error)
    {
        if (handshakeCompletion.TryRemove(client.Id, out var tcs))
            tcs.TrySetException(new IOException($"Socket error before TLS handshake completed: {error}"));
        logger.Error($"Error on client socket {error}");
        DisconnectClient(client);
    }

    public void OnReceived(Client client, byte[] buffer, long offset, long size)
    {
        try
        {
            if (!connectionBuffers.TryGetValue(client.Id, out var sb)) return;
            sb.Append(buffer, (int)offset, (int)size);

            foreach (var socketMessage in GetMessagesFromBuffer(sb, logger))
            {
                if (socketMessage is Authentication authMessage)
                {
                    var deviceAuth = StartAuth(client.Id);
                    AuthenticateClientAsync(client, authMessage, deviceAuth);
                    continue;
                }

                if (deviceAuths.TryGetValue(client.Id, out var active))
                {
                    active.Deferred.Enqueue(socketMessage);
                    continue;
                }

                RouteMessage(client.Id, socketMessage);
            }
        }
        catch (Exception ex)
        {
            logger.Error($"Error in OnReceived for client", ex);
        }
    }
    #endregion

    #region Client Authentication

    private async void AuthenticateClientAsync(Client client, Authentication authMessage, DeviceAuth deviceAuth)
    {
        try
        {
            await HandleServerAuthentication(client, authMessage, deviceAuth.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.Debug($"Auth superseded for client {client.Id}");
        }
        finally
        {
            FinalizeAuth(client.Id, deviceAuth);
        }
    }

    private async Task HandleServerAuthentication(Client client, Authentication authMessage, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            IPEndPoint? endPoint = client.Socket.RemoteEndPoint as IPEndPoint;
            var address = endPoint?.Address.ToString();

            if (string.IsNullOrEmpty(address)) return;

            logger.Info($"Received AuthenticationMessage from server at {address}");

            var localDevice = await deviceManager.GetLocalDeviceAsync();
            if (authMessage.DeviceId == localDevice.DeviceId)
                throw new InvalidOperationException("Cannot connect to this device itself");

            var pairedDevice = PairedDevices.FirstOrDefault(d => d.Id == authMessage.DeviceId);
            if (expectedClientDevices.TryGetValue(client.Id, out var expectedDeviceId) && expectedDeviceId != authMessage.DeviceId)
                throw new InvalidOperationException("Peer identity differs from the requested device");
            if (pairedDevice is not null)
            {
                var certificate = SslHelper.GetCertForPublicKey(authMessage.PublicKey);
                if (certificate is null || !certificate.AsSpan().SequenceEqual(pairedDevice.Certificate))
                    throw new InvalidOperationException("Certificate verification failed for paired device");
                pairedDevice.DeviceType = authMessage.DeviceType ?? pairedDevice.DeviceType;
                if (authMessage.Port > 0) pairedDevice.Port = authMessage.Port;
                if (!await AdoptConnection(pairedDevice, null, client, address, localDevice.DeviceId,
                    authMessage.UserInitiated, cancellationToken))
                {
                    DisconnectClient(client);
                    return;
                }
                if (authenticationCompletion.TryGetValue(client.Id, out var completion)) completion.TrySetResult(true);
                ConnectionStatusChanged?.Invoke(this, pairedDevice);
                await deviceManager.UpdateDevice(pairedDevice);
            }
            else
            {
                // New device: we stashed the server cert during TLS; look it up by PublicKey and store on DiscoveredDevice.
                var certificate = SslHelper.GetCertForPublicKey(authMessage.PublicKey);
                if (certificate is null || certificate.Length == 0)
                {
                    throw new Exception("No server certificate or PublicKey mismatch; rejecting");
                }
                await AddDiscoveredDevice(client, authMessage, address, endPoint?.Port ?? 5150, certificate);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.Error($"Error in client authentication", ex);
            DisconnectClient(client);
        }
    }

    private async Task AddDiscoveredDevice(Client client, Authentication authMessage, string address, int port, byte[] certificate)
    {
        var verificationKey = SslHelper.GetVerificationCode(authMessage.PublicKey);

        var existingDevice = DiscoveredDevices.FirstOrDefault(d => d.Id == authMessage.DeviceId);
        if (existingDevice is not null && existingDevice.Session is not null)
        {
            DisconnectSession(existingDevice.Session);
        }

        var device = new DiscoveredDevice
        {
            Id = authMessage.DeviceId,
            Name = authMessage.DeviceName,
            Model = authMessage.Model,
            DeviceType = authMessage.DeviceType,
            Address = address,
            Port = port,
            Certificate = certificate,
            VerificationKey = verificationKey,
            Client = client
        };

        await App.MainWindow.DispatcherQueue.EnqueueAsync(() => DiscoveredDevices.Add(device));
    }
    #endregion

    #endregion
}
