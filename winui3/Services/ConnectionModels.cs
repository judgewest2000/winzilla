using System.Text.Json.Serialization;

namespace FtpZilla.Services;

public enum Protocol { Sftp, Ftp }

/// <summary>A named, coloured set of connections (e.g. Production, Develop). The connected window takes on the group's colour.</summary>
public sealed class ConnectionGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Group";

    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#2EA043";

    /// <summary>Browse and download only: uploading, deleting, renaming, moving and creating folders are disabled.</summary>
    public bool IsReadOnly { get; set; }

    public List<SavedConnection> Connections { get; set; } = [];

    /// <summary>The name with a "(read-only)" suffix, unless the name already says so.</summary>
    [JsonIgnore]
    public string LabelWithReadOnly => IsReadOnly && !Name.Contains("read", StringComparison.OrdinalIgnoreCase) ? $"{Name} (read-only)" : Name;
}

/// <summary>
/// One FTP/SFTP server. The password is held in memory as plain text and written to disk encrypted with DPAPI (current user).
/// </summary>
public sealed class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Optional display name; the tree falls back to user@host.</summary>
    public string Name { get; set; } = "";

    public Protocol Protocol { get; set; } = Protocol.Sftp;
    public string Host { get; set; } = "";
    public int Port { get; set; } = DefaultPort(Protocol.Sftp);
    public string User { get; set; } = "";

    /// <summary>Mirrors the owning group's <see cref="ConnectionGroup.IsReadOnly"/> (kept in step by <see cref="ConnectionStore"/>).</summary>
    [JsonIgnore] public bool IsReadOnly { get; set; }

    [JsonIgnore] public string Password { get; set; } = "";

    [JsonPropertyName("password")]
    public string ProtectedPassword
    {
        get => SecretProtector.Protect(Password);
        set => Password = SecretProtector.Unprotect(value);
    }

    [JsonIgnore]
    public string Address => User.Length > 0 ? $"{User}@{Host}" : Host;

    [JsonIgnore]
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : Host.Length > 0 ? Address : "(no host)";

    /// <summary>"SFTP · user@host" (with the port when it isn't the protocol's default).</summary>
    [JsonIgnore]
    public string Summary => $"{(Protocol == Protocol.Sftp ? "SFTP" : "FTP")} · {Address}{(Port != DefaultPort(Protocol) ? $":{Port}" : "")}";

    public static int DefaultPort(Protocol protocol) => protocol == Protocol.Sftp ? 22 : 21;

    /// <summary>True when this saved connection points at the given server and account.</summary>
    public bool Matches(Protocol protocol, string host, int port, string user) =>
        Protocol == protocol && Port == port
        && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(User, user, StringComparison.Ordinal);

    public SavedConnection Clone() => new()
    {
        Id = Id,
        Name = Name,
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        User = User,
        Password = Password,
        IsReadOnly = IsReadOnly,
    };
}
