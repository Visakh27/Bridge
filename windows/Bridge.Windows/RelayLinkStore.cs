using System.Security.Cryptography;
using System.Text;
using Bridge.Network;

namespace Bridge.Windows;

/// <summary>
/// Keeps the relay link (which includes the relay key) encrypted for the current Windows user
/// (DPAPI), apart from settings.json. A missing or unreadable file means "no relay".
/// </summary>
internal sealed class RelayLinkStore(string path)
{
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("bridge-relay-link-v1");

    public RelayLink? Load()
    {
        try
        {
            if (!File.Exists(path)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return RelayLink.Parse(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    /// <summary>Saves (or with null, deletes) the link. False if it couldn't be written.</summary>
    public bool Save(RelayLink? link)
    {
        try
        {
            if (link is null) { if (File.Exists(path)) File.Delete(path); return true; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(Encoding.UTF8.GetBytes(link.ToString()), Entropy, DataProtectionScope.CurrentUser));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return false; }
    }
}
