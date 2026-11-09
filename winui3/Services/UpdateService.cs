using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Winzilla.Services;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string Sha256);

/// <summary>Checks GitHub Releases for a newer version and runs its Setup.exe.</summary>
public static class UpdateService
{
    private const string Repo = "judgewest2000/winzilla";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        await using var stream = await Http.GetStreamAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        if (!Version.TryParse(root.GetProperty("tag_name").GetString()?.TrimStart('v'), out var latest)
            || Normalize(latest) <= CurrentVersion)
            return null;

        var assetName = $"Winzilla-Setup-{ArchitectureName()}.exe";
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != assetName) continue;

            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return null;

            return new UpdateInfo(Normalize(latest), asset.GetProperty("browser_download_url").GetString()!, digest[7..]);
        }
        return null;
    }

    /// <summary>Downloads the installer and verifies its SHA-256 against the digest GitHub published.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Winzilla-Setup-{update.Version}.exe");

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, ct);
        }

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));

        if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded update failed its integrity check and was discarded.");
        }
        return path;
    }

    /// <summary>Runs the installer silently; it closes this app, upgrades in place and relaunches it.</summary>
    public static void StartInstaller(string path) =>
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RELAUNCH=1")
        {
            UseShellExecute = true,
        });

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string ArchitectureName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Winzilla-updater");
        return client;
    }
}
