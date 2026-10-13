using System.Security.Cryptography;

namespace Bridge.Security;

/// <summary>
/// Long-term device identity: a NIST P-256 key used for ECDSA-SHA256
/// signatures over the handshake transcript (see protocol/protocol.md). The
/// Android side uses the same curve, hash and DER signature encoding, so each
/// platform can verify the other. Public keys travel as SubjectPublicKeyInfo.
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

    /// <param name="deviceId">Normally omitted (a new random id); tests pass one to simulate impersonation.</param>
    public static DeviceIdentity Generate(string? deviceId = null)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var priv = ecdsa.ExportPkcs8PrivateKey();
        var pub = ecdsa.ExportSubjectPublicKeyInfo();
        return new DeviceIdentity(string.IsNullOrEmpty(deviceId) ? Guid.NewGuid().ToString() : deviceId, priv, pub);
    }

    /// <summary>
    /// Loads (or creates and persists) the identity from a DPAPI-protected
    /// file under the app's local data folder. Windows-only (DPAPI).
    /// </summary>
    public static DeviceIdentity LoadOrCreate(string storePath)
    {
        if (File.Exists(storePath))
        {
            try
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
                var loaded = new DeviceIdentity(deviceId, priv, pub);
                loaded.Sign([0]); // proves the stored key is usable
                return loaded;
            }
            catch (Exception ex) when (ex is CryptographicException or EndOfStreamException or IOException)
            {
                // Corrupt or foreign identity file: fall through and mint a new
                // identity (previously paired phones will need to re-pair).
            }
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

    /// <summary>ECDSA P-256 / SHA-256 signature, DER-encoded (what Java's SHA256withECDSA produces).</summary>
    public byte[] Sign(byte[] data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(PrivateKeyBlob, out _);
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>Verifies a DER-encoded ECDSA-SHA256 signature against a SubjectPublicKeyInfo key. Never throws.</summary>
    public static bool Verify(byte[] publicKeySpki, byte[] data, byte[] signature)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
