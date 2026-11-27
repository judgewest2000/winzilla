using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winzilla.Services;

/// <summary>
/// Preferences and known SFTP host keys, in %LOCALAPPDATA%\Winzilla\settings.json.
/// Saved connections and their passwords live in <see cref="ConnectionStore"/>.
/// </summary>
public sealed class Settings
{
    private static readonly string FilePath = Path.Combine(ConnectionStore.DefaultRoot, "settings.json");

    private readonly object _sync = new();

    /// <summary>The quick-connect form as last used (never the password).</summary>
    public Protocol Protocol { get; set; } = Protocol.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public Dictionary<string, string> KnownHosts { get; set; } = [];

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), ConnectionStore.JsonOptions) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(this, ConnectionStore.JsonOptions));
                File.Move(temp, FilePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Trust-on-first-use: new hosts are remembered, changed keys are rejected.</summary>
    public bool VerifyHostKey(string host, int port, string keyName, string fingerprint, out string? error)
    {
        var id = $"{host}:{port}:{keyName}";
        lock (_sync)
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
