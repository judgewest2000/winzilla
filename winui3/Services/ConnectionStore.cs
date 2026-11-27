using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winzilla.Services;

/// <summary>Persists connection groups to %LOCALAPPDATA%\Winzilla\connections.json.</summary>
public sealed class ConnectionStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public ConnectionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? DefaultRoot;
    }

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winzilla");

    public string RootDirectory { get; }
    private string FilePath => Path.Combine(RootDirectory, "connections.json");

    /// <summary>Loads the groups; on first run creates Production / Develop / Read-only groups.</summary>
    public List<ConnectionGroup> Load()
    {
        var groups = LoadFile();
        ApplyReadOnly(groups);
        return groups;
    }

    private List<ConnectionGroup> LoadFile()
    {
        if (!File.Exists(FilePath)) return DefaultGroups();
        try
        {
            return JsonSerializer.Deserialize<List<ConnectionGroup>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection rather than silently overwriting it on the next save.
            File.Copy(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return DefaultGroups();
        }
    }

    /// <summary>Saves the groups, first copying each group's read-only flag onto its connections (every edit is followed by a save).</summary>
    public void Save(IEnumerable<ConnectionGroup> groups)
    {
        var list = groups.ToList();
        ApplyReadOnly(list);
        Directory.CreateDirectory(RootDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static void ApplyReadOnly(IEnumerable<ConnectionGroup> groups)
    {
        foreach (var group in groups)
            foreach (var connection in group.Connections) connection.IsReadOnly = group.IsReadOnly;
    }

    public static List<ConnectionGroup> DefaultGroups() =>
    [
        new() { Name = "Production", Color = "#C42B1C" },
        new() { Name = "Develop", Color = "#2EA043" },
        new() { Name = "Read-only", Color = "#2575D6", IsReadOnly = true },
    ];
}
