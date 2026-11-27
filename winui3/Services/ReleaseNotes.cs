using System.Text.RegularExpressions;

namespace FtpZilla.Services;

public enum NoteStyle { Plain, Bold, Italic, Code }

public sealed record NoteSpan(string Text, NoteStyle Style);

/// <summary>A bullet in a release; <see cref="Level"/> 0 is top level, 1 is nested.</summary>
public sealed record NoteItem(int Level, IReadOnlyList<NoteSpan> Spans);

public sealed record ReleaseNote(string Version, string? Date, IReadOnlyList<NoteItem> Items);

/// <summary>Parses CHANGELOG.md: "## version (date)" headings followed by "- " bullets with **bold**, *italic* and `code`.</summary>
public static partial class ReleaseNotes
{
    public static IReadOnlyList<ReleaseNote> Parse(string markdown)
    {
        var releases = new List<ReleaseNote>();
        string? version = null, date = null;
        var items = new List<NoteItem>();

        void Flush()
        {
            if (version is not null) releases.Add(new ReleaseNote(version, date, items));
            items = [];
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                var heading = HeadingPattern().Match(line[3..].Trim());
                version = heading.Groups["v"].Value;
                date = heading.Groups["d"].Success ? heading.Groups["d"].Value : null;
            }
            else if (version is not null && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))
            {
                var indent = line.Length - line.TrimStart().Length;
                items.Add(new NoteItem(indent >= 2 ? 1 : 0, Inline(line.TrimStart()[2..])));
            }
        }
        Flush();
        return releases;
    }

    public static IReadOnlyList<NoteSpan> Inline(string text)
    {
        var spans = new List<NoteSpan>();
        var last = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > last) spans.Add(new NoteSpan(text[last..m.Index], NoteStyle.Plain));
            if (m.Groups["b"].Success) spans.Add(new NoteSpan(m.Groups["b"].Value, NoteStyle.Bold));
            else if (m.Groups["c"].Success) spans.Add(new NoteSpan(m.Groups["c"].Value, NoteStyle.Code));
            else spans.Add(new NoteSpan(m.Groups["i"].Value, NoteStyle.Italic));
            last = m.Index + m.Length;
        }
        if (last < text.Length) spans.Add(new NoteSpan(text[last..], NoteStyle.Plain));
        return spans;
    }

    [GeneratedRegex(@"^(?<v>\S+)(?:\s+\((?<d>[^)]*)\))?")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\*\*(?<b>.+?)\*\*|`(?<c>[^`]+)`|\*(?<i>[^*]+)\*")]
    private static partial Regex InlinePattern();
}
