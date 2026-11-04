using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Sefirah.Utils;

internal static class TlsIdentityChecks
{
    public static async Task RunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sefirah-tls-test-{Guid.NewGuid():N}.pfx");
        var brokenPath = path + ".invalid";
        using var identity = CreateIdentity();
        using var peer = CreateIdentity();
        try
        {
            var store = new CertificateIdentityStore();
            var creations = 0;
            var certificates = new X509Certificate2[32];
            Parallel.For(0, certificates.Length, index => certificates[index] = store.GetOrCreate(path, () =>
            {
                Interlocked.Increment(ref creations);
                File.WriteAllBytes(path, identity.Export(X509ContentType.Pfx));
                return identity;
            }));
            Check(creations == 1 && certificates.All(cert => ReferenceEquals(cert, identity)),
                "Concurrent discovery must not generate conflicting TLS identities");
            using var reloaded = new CertificateIdentityStore().GetOrCreate(path, () => throw new Exception("Identity was replaced"));
            Check(reloaded.HasPrivateKey && reloaded.RawData.SequenceEqual(identity.RawData), "Identity survives restart");

            Exception? reported = null;
            var failedStore = new CertificateIdentityStore();
            var survived = await AsyncOperationGuard.RunAsync(() =>
            {
                failedStore.GetOrCreate(brokenPath, () => throw new CryptographicException("KeyIso unavailable"));
                return Task.CompletedTask;
            }, error => reported = error);
            Check(!survived && reported is CryptographicException, "Crypto failure before the first await must not escape a discovery event");
            Check(ReferenceEquals(failedStore.GetOrCreate(brokenPath, () => peer), peer), "Restoring the Windows crypto service permits a fresh retry");

            File.WriteAllText(brokenPath, "invalid existing identity");
            var rotated = false;
            var loaded = await AsyncOperationGuard.RunAsync(() =>
            {
                new CertificateIdentityStore().GetOrCreate(brokenPath, () => { rotated = true; return peer; });
                return Task.CompletedTask;
            }, _ => { });
            Check(!loaded && !rotated && File.ReadAllText(brokenPath) == "invalid existing identity",
                "A failed identity load must not silently replace the pinned certificate");

            // Real mutual TLS with pinned identities, not just a public-key serialization test.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var tcp = await listener.AcceptTcpClientAsync();
                    using var tls = new SslStream(tcp.GetStream(), false,
                        (_, certificate, _, _) => certificate?.GetRawCertData().SequenceEqual(peer.RawData) == true);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    });
                    var input = new byte[1];
                    await tls.ReadExactlyAsync(input);
                    Check(input[0] == 42, "Authenticated application traffic received");
                });
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var stream = new SslStream(client.GetStream(), false,
                    (_, certificate, _, _) => certificate?.GetRawCertData().SequenceEqual(identity.RawData) == true);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ClientCertificates = new X509CertificateCollection { peer },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }).WaitAsync(TimeSpan.FromSeconds(5));
                await stream.WriteAsync(new byte[] { 42 });
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { listener.Stop(); }
            Console.WriteLine("PASS: concurrent TLS identity, restart persistence, unavailable KeyIso, guarded discovery, no silent identity rotation and real pinned mutual TLS.");
        }
        finally
        {
            File.Delete(path);
            File.Delete(brokenPath);
        }
    }

    private static X509Certificate2 CreateIdentity()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=SefirahRegression", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Match production's PFX import: Schannel needs an imported key, not the transient key
        // backing CertificateRequest.CreateSelfSigned. Dispose removes the temporary container.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
