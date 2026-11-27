using FtpZilla.Services;

namespace FtpZilla.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public void Parse_HeadingsAndNestedBullets_ReturnsVersionsDatesAndLevels()
    {
        var notes = ReleaseNotes.Parse("# Release notes\r\n\r\n## 1.1.0 (2026-11-18)\r\n- **Big** thing\r\n  - detail\r\n\r\n## 1.0.0\n- First\n");

        Assert.Equal(2, notes.Count);
        Assert.Equal("1.1.0", notes[0].Version);
        Assert.Equal("2026-11-18", notes[0].Date);
        Assert.Equal([0, 1], notes[0].Items.Select(i => i.Level));
        Assert.Equal("1.0.0", notes[1].Version);
        Assert.Null(notes[1].Date);
        Assert.Equal("First", Assert.Single(Assert.Single(notes[1].Items).Spans).Text);
    }

    [Fact]
    public void Inline_MixedFormatting_SplitsIntoStyledSpans()
    {
        var spans = ReleaseNotes.Inline("Use **Open with**, `-Flag` or *Close* now");

        Assert.Equal(
            [
                new NoteSpan("Use ", NoteStyle.Plain), new NoteSpan("Open with", NoteStyle.Bold), new NoteSpan(", ", NoteStyle.Plain),
                new NoteSpan("-Flag", NoteStyle.Code), new NoteSpan(" or ", NoteStyle.Plain), new NoteSpan("Close", NoteStyle.Italic),
                new NoteSpan(" now", NoteStyle.Plain),
            ],
            spans);
    }

    [Fact]
    public void Parse_ShippedChangelog_StartsWithProjectVersion()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "CHANGELOG.md"))) root = root.Parent!;
        var csproj = File.ReadAllText(Path.Combine(root.FullName, "winui3", "FtpZilla.csproj"));
        var version = System.Text.RegularExpressions.Regex.Match(csproj, "<Version>(.+?)</Version>").Groups[1].Value;

        var notes = ReleaseNotes.Parse(File.ReadAllText(Path.Combine(root.FullName, "CHANGELOG.md")));

        Assert.Equal(version, notes[0].Version);
    }
}
