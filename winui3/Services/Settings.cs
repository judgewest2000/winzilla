using System.Text.Json;

namespace Winzilla.Services;

/// <summary>Persisted preferences. Passwords are never stored.</summary>
public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Winzilla", "settings.json");

    private static readonly object Sync = new();

    public Protocol Protocol { get; set; } = Protocol.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public List<SavedConnection> Connections { get; set; } = [];
    public Dictionary<string, string> KnownHosts { get; set; } = [];

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    /// <summary>Trust-on-first-use: new hosts are remembered, changed keys are rejected.</summary>
    public bool VerifyHostKey(string host, int port, string keyName, string fingerprint, out string? error)
    {
        var id = $"{host}:{port}:{keyName}";
        lock (Sync)
        {
            if (KnownHosts.TryGetValue(id, out var known))
            {
                if (known == fingerprint) { error = null; return true; }
                error = $"The host key for {host} has changed (expected {known}, got {fingerprint}). Connection refused.";
                return false;
            }
            KnownHosts[id] = fingerprint;
        }
        Save();
        error = null;
        return true;
    }
}
