using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sefirah.Utils;

/// <summary>One TLS identity per process; temporary key-store failures must never rotate it.</summary>
public sealed class CertificateIdentityStore
{
    private readonly object gate = new();
    private X509Certificate2? certificate;

    public X509Certificate2 GetOrCreate(string path, Func<X509Certificate2> create)
    {
        lock (gate)
        {
            if (certificate is not null) return certificate;
            // An unreadable existing identity is an error, not permission to silently replace it.
            var loaded = File.Exists(path) ? X509CertificateLoader.LoadPkcs12FromFile(path, null) : create();
            if (!loaded.HasPrivateKey)
            {
                loaded.Dispose();
                throw new CryptographicException("The TLS identity has no private key.");
            }
            return certificate = loaded;
        }
    }
}
