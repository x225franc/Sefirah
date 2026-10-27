global using System.Text.Json;
global using System.Text.Json.Serialization;
global using Sefirah.Data.Enums;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sefirah.Data.Models;
using Sefirah.Utils;
using Sefirah.Utils.Serialization;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var original = "{\"type\":\"ClipboardInfo\",\"content\":\"été 😀 漢字\",\"clipboardType\":\"text/plain\"}";
var bytes = Encoding.UTF8.GetBytes(original + "\n");
for (int split = 0; split <= bytes.Length; split++)
{
    var buffer = new WireMessageBuffer();
    buffer.Append(bytes, 0, split);
    var messages = buffer.ReadMessages();
    buffer.Append(bytes, split, bytes.Length - split);
    messages.AddRange(buffer.ReadMessages());
    Check(messages.SequenceEqual(new[] { original }), $"UTF8 split {split}");
    Check(buffer.ReadMessages().Count == 0, "Messages consumed");
}
var batch = new WireMessageBuffer();
var joined = Encoding.UTF8.GetBytes("\r\n" + original + "\r\n" + original + "\npartial");
batch.Append(joined, 0, joined.Length);
Check(batch.ReadMessages().SequenceEqual(new[] { original, original }), "Coalesced messages");
var tail = Encoding.UTF8.GetBytes(" end\n");
batch.Append(tail, 0, tail.Length);
Check(batch.ReadMessages().Single() == "partial end", "Tail retained");
var legacy = JsonMessageSerializer.DeserializeMessage("{\"type\":\"Authentication\",\"deviceId\":\"phone\",\"deviceName\":\"Android\",\"publicKey\":\"key\",\"model\":\"Pixel\"}") as Authentication;
Check(legacy is { DeviceType: null, Port: 0 }, "Legacy Android authentication");
Check(legacy is { UserInitiated: false }, "Legacy auth cannot override manual pause");
var explicitAuth = new Authentication { DeviceId = "phone", DeviceName = "Android", Model = "Pixel", PublicKey = "key", UserInitiated = true };
Check(JsonMessageSerializer.Serialize(explicitAuth).Contains("\"userInitiated\":true"), "Explicit connect flag uses Android wire casing");
Check(JsonMessageSerializer.DeserializeMessage(JsonMessageSerializer.Serialize(explicitAuth)) is Authentication { UserInitiated: true }, "Explicit connection flag survives serialization");

// A discovery storm must neither replace a healthy transport nor undo a user's pause.
Check(!ConnectionOwnershipPolicy.CanAdopt("a", "b", true, true, false, false, false, false), "Automatic incoming auth cannot undo pause");
Check(ConnectionOwnershipPolicy.CanAdopt("a", "b", true, true, true, false, false, false), "Explicit peer connection can resume a paused link");
Check(!ConnectionOwnershipPolicy.CanAdopt("a", "b", true, false, false, true, false, false), "Duplicate incoming connection is rejected");
Check(!ConnectionOwnershipPolicy.CanAdopt("a", "b", false, false, false, false, true, false), "Duplicate outgoing connection is rejected");
foreach (var local in new[] { "a", "z" })
{
    var remote = local == "a" ? "z" : "a";
    var acceptsIncoming = ConnectionOwnershipPolicy.CanAdopt(local, remote, true, false, false, false, false, true);
    Check(acceptsIncoming == (local == "z"), "Simultaneous opens agree on lower-ID initiator");
    Check(ConnectionOwnershipPolicy.CanAdopt(local, remote, true, false, false, false, false, false), "Either direction works in isolation");
    Check(ConnectionOwnershipPolicy.CanAdopt(local, remote, false, false, false, true, false, true) == (local == "a"), "Only preferred direction replaces opposite socket");
}

var health = new ConnectionLiveness();
health.Received(1_000);
Check(health.Check(60_999) == LivenessAction.None, "No idle probe before 60 seconds");
Check(health.Check(61_000) == LivenessAction.Ping, "Exactly one probe after idle");
Check(health.Check(61_001) == LivenessAction.None && health.Check(120_999) == LivenessAction.None, "Repeated wakes cannot duplicate a pending probe");
Check(health.Check(121_000) == LivenessAction.Disconnect, "Unanswered probe expires after grace");
health.Received(121_001);
Check(health.Check(121_001) == LivenessAction.None, "Any valid traffic clears pending timeout");
health.Received(500_000);
Check(health.Check(9_000_000) == LivenessAction.Ping && health.Check(9_000_001) == LivenessAction.None, "Resume starts a fresh probe rather than expiring idle age");

var retries = new ConnectionRetrySchedule();
Check(retries.IsDue("phone", 0), "First retry is immediate");
long retryAt = 0;
foreach (var delay in new[] { 15_000, 30_000, 60_000, 120_000, 240_000, 300_000, 300_000 })
{
    retries.Failed("phone", retryAt);
    Check(!retries.IsDue("phone", retryAt + delay - 1), "Discovery respects failure backoff");
    Check(retries.IsDue("phone", retryAt + delay), "Retry becomes eligible exactly at deadline");
    Check(retries.IsDue("other", retryAt), "Each device retries independently");
    retryAt += delay;
}
retries.Reset("phone");
Check(retries.IsDue("phone", 0), "Manual reconnect and success clear backoff");
retries.Failed("phone", 0);
retries.ResetAll();
Check(retries.IsDue("phone", 0), "Network recovery clears obsolete retry deadlines");
Console.WriteLine("PASS: manual pause protocol, simultaneous-open arbitration, idle-only liveness, bounded probes and shared retry backoff.");
var auth = new Authentication { DeviceId = "pc", DeviceName = "PC", Model = "Windows", PublicKey = "key", DeviceType = "windows", Port = 5162 };
Check(JsonMessageSerializer.DeserializeMessage(JsonMessageSerializer.Serialize(auth)) is Authentication { DeviceType: "windows", Port: 5162 }, "Desktop authentication roundtrip");
foreach (var a in new[] { "a", "b", "c" })
foreach (var b in new[] { "a", "b", "c" })
{
    var ab = DesktopPeerPolicy.ShouldInitiate(a, b, "windows");
    var ba = DesktopPeerPolicy.ShouldInitiate(b, a, "windows");
    Check(a == b ? !ab : ab != ba, "Exactly one desktop initiator");
}
Check(DesktopPeerPolicy.ShouldInitiate("z", "phone", null), "Legacy discovery preserved");
Check(DesktopPeerPolicy.IsDesktop("WINDOWS") && DesktopPeerPolicy.IsDesktop("desktop"), "Desktop type normalization");
var image = new byte[2 * 1024 * 1024 + 37];
Random.Shared.NextBytes(image);
var payloads = new[] {
    new ClipboardInfo { ClipboardType = "text/plain", Content = "Bonjour 😀\nDeuxième ligne" },
    new ClipboardInfo { ClipboardType = "image/png", Content = Convert.ToBase64String(image) }
};
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var tasks = Enumerable.Range(0, 3).Select(async _ =>
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await listener.AcceptTcpClientAsync();
        var send = Task.Run(async () =>
        {
            foreach (var payload in payloads)
                await server.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonMessageSerializer.Serialize(payload) + "\n"));
            server.Client.Shutdown(SocketShutdown.Send);
        });
        var incoming = new WireMessageBuffer();
        var received = new List<ClipboardInfo>();
        var chunk = new byte[4093];
        int count;
        while ((count = await client.GetStream().ReadAsync(chunk)) != 0)
        {
            incoming.Append(chunk, 0, count);
            received.AddRange(incoming.ReadMessages().Select(m => (ClipboardInfo)JsonMessageSerializer.DeserializeMessage(m)!));
        }
        await send;
        Check(received.Count == 2 && received[0].Content == payloads[0].Content, "Multi-peer text transfer");
        Check(Convert.FromBase64String(received[1].Content).SequenceEqual(image), "Large image integrity");
    }).ToArray();
    await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
}
finally { listener.Stop(); }
Console.WriteLine("PASS: UTF8 fragmentation, batching, Android compatibility, desktop roles, three TCP peers, text and >2 MB image integrity.");
