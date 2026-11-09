using Windows.Security.Credentials;

namespace Winzilla.Services;

/// <summary>Stores connection passwords in the Windows Credential Locker (PasswordVault), keyed by connection id.</summary>
public static class CredentialStore
{
    private const string Resource = "Winzilla";

    private static readonly PasswordVault Vault = new();

    public static string? Get(string id)
    {
        try
        {
            var credential = Vault.Retrieve(Resource, id);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch { return null; }
    }

    public static void Set(string id, string password)
    {
        if (password.Length == 0) { Remove(id); return; }
        Vault.Add(new PasswordCredential(Resource, id, password));
    }

    public static void Remove(string id)
    {
        try { Vault.Remove(Vault.Retrieve(Resource, id)); }
        catch { }
    }
}
