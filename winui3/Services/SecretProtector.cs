using System.Security.Cryptography;
using System.Text;

namespace Winzilla.Services;

/// <summary>Encrypts secrets at rest with DPAPI (current user), so the connections file is useless on another account or machine.</summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Winzilla.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || !OperatingSystem.IsWindows()) return plain ?? "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    /// <summary>Returns "" when the value can't be decrypted (e.g. the file was copied from another user).</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) return stored;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
