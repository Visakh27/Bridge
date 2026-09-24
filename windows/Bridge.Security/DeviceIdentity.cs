using System.Security.Cryptography;

namespace Bridge.Security;

/// <summary>
/// Long-term device identity keypair (X25519 via ECDiffieHellman on the P-256
/// curve is not X25519; .NET's built-in ECDH is used here as a portable
/// stand-in — swap for libsodium/BouncyCastle X25519 if strict X25519 is
/// required end-to-end. The wire format only cares about the base64 public
/// key blob, so the underlying curve is an implementation detail per platform
/// as long as both sides of *one* connection agree - Windows talks to Windows
/// only for identity storage; the cross-platform ECDH handshake in
/// SessionCrypto uses the NIST P-256 curve on both Windows and Android for
/// interoperability.
/// </summary>
public sealed class DeviceIdentity
{
    public string DeviceId { get; }
    public byte[] PrivateKeyBlob { get; }
    public byte[] PublicKeyBlob { get; }

    private DeviceIdentity(string deviceId, byte[] priv, byte[] pub)
    {
        DeviceId = deviceId;
        PrivateKeyBlob = priv;
        PublicKeyBlob = pub;
    }

    public static DeviceIdentity Generate()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdh.ExportPkcs8PrivateKey();
        var pub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        return new DeviceIdentity(Guid.NewGuid().ToString(), priv, pub);
    }

    /// <summary>
    /// Loads (or creates and persists) the identity from a DPAPI-protected
    /// file under the app's local data folder. Never call this on a
    /// background thread pool worker holding the private key longer than
    /// necessary.
    /// </summary>
    public static DeviceIdentity LoadOrCreate(string storePath)
    {
        if (File.Exists(storePath))
        {
            var protectedBytes = File.ReadAllBytes(storePath);
            var unprotected = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            using var ms = new MemoryStream(unprotected);
            using var reader = new BinaryReader(ms);
            var deviceId = reader.ReadString();
            var privLen = reader.ReadInt32();
            var priv = reader.ReadBytes(privLen);
            var pubLen = reader.ReadInt32();
            var pub = reader.ReadBytes(pubLen);
            return new DeviceIdentity(deviceId, priv, pub);
        }

        var identity = Generate();
        identity.Save(storePath);
        return identity;
    }

    public void Save(string storePath)
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(DeviceId);
            writer.Write(PrivateKeyBlob.Length);
            writer.Write(PrivateKeyBlob);
            writer.Write(PublicKeyBlob.Length);
            writer.Write(PublicKeyBlob);
        }

        var protectedBytes = ProtectedData.Protect(ms.ToArray(), null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        File.WriteAllBytes(storePath, protectedBytes);
    }

    public ECDiffieHellman ToEcdh()
    {
        var ecdh = ECDiffieHellman.Create();
        ecdh.ImportPkcs8PrivateKey(PrivateKeyBlob, out _);
        return ecdh;
    }
}
