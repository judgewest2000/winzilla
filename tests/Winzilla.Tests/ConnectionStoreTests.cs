using System.Text.Json;
using Winzilla.Services;

namespace Winzilla.Tests;

public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Winzilla.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void StoreRoundTripsAndEncryptsPasswords()
    {
        var store = new ConnectionStore(_root);
        var connection = new SavedConnection { Name = "Live", Protocol = Protocol.Sftp, Host = "example.com", Port = 22, User = "deploy", Password = "s3cret-pass" };
        var group = new ConnectionGroup { Name = "Production", Color = "#C42B1C", IsReadOnly = true, Connections = [connection] };

        store.Save([group]);

        var json = File.ReadAllText(Path.Combine(_root, "connections.json"));
        Assert.DoesNotContain("s3cret-pass", json);
        Assert.Contains("dpapi:", json);

        var loaded = Assert.Single(store.Load());
        Assert.Equal("Production", loaded.Name);
        Assert.True(loaded.IsReadOnly);
        var saved = Assert.Single(loaded.Connections);
        Assert.Equal("s3cret-pass", saved.Password);
        Assert.Equal("example.com", saved.Host);
        Assert.True(saved.IsReadOnly);
    }

    [Fact]
    public void MissingFileGivesDefaultGroups()
    {
        var groups = new ConnectionStore(_root).Load();

        Assert.Equal(["Production", "Develop", "Read-only"], groups.Select(g => g.Name));
        Assert.True(groups.Single(g => g.Name == "Read-only").IsReadOnly);
    }

    [Fact]
    public void CorruptFileIsKeptAndDefaultsReturned()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "connections.json"), "{ not json");

        var groups = new ConnectionStore(_root).Load();

        Assert.NotEmpty(groups);
        Assert.Single(Directory.GetFiles(_root, "connections.json.corrupt-*"));
    }

    [Fact]
    public void UnreadableSecretGivesEmptyPassword()
    {
        Assert.Equal("", SecretProtector.Unprotect("dpapi:not-base64!"));
        var connection = JsonSerializer.Deserialize<SavedConnection>("""{"host":"h","password":"dpapi:AAAA"}""", ConnectionStore.JsonOptions)!;
        Assert.Equal("", connection.Password);
    }
}
