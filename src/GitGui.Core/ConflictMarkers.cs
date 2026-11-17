namespace GitGui.Core;

public enum ConflictChoice { Ours, Theirs, Both }
public sealed record ConflictSection(int Id, int Start, int Length, string Ours, string Theirs, string? Base)
{
    public string Label
    {
        get
        {
            var end = Ours.IndexOf('\n');
            return $"{Id + 1} · {(end < 0 ? Ours : Ours[..end]).TrimEnd('\r')}";
        }
    }
}

/// <summary>Parses Git's normal and diff3 markers while preserving original newline bytes.</summary>
public static class ConflictMarkers
{
    public static IReadOnlyList<ConflictSection> Parse(string text, CancellationToken ct = default)
    {
        var sections = new List<ConflictSection>();
        var begin = -1; var oursStart = 0; var baseAt = -1; var baseStart = 0; var split = -1; var theirsStart = 0;
        var lineNumber = 0;
        for (var start = 0; start < text.Length;)
        {
            if ((lineNumber++ & 255) == 0) ct.ThrowIfCancellationRequested();
            var next = text.IndexOf('\n', start);
            var end = next < 0 ? text.Length : next + 1;
            var line = text.AsSpan(start, end - start);
            if (Marker(line, '<'))
            {
                begin = start; oursStart = end; baseAt = split = -1;
            }
            else if (begin >= 0 && split < 0 && Marker(line, '|')) { baseAt = start; baseStart = end; }
            else if (begin >= 0 && split < 0 && Marker(line, '=')) { split = start; theirsStart = end; }
            else if (begin >= 0 && split >= 0 && Marker(line, '>'))
            {
                sections.Add(new(sections.Count, begin, end - begin, text[oursStart..(baseAt < 0 ? split : baseAt)],
                    text[theirsStart..start], baseAt < 0 ? null : text[baseStart..split]));
                begin = -1;
            }
            start = end;
        }
        return sections;
    }
    public static string Replace(string text, ConflictSection selected, ConflictChoice choice)
    {
        if (selected.Start < 0 || selected.Length < 0 || selected.Start > text.Length - selected.Length)
            throw new InvalidOperationException("The conflict section changed. Select it again.");
        var actual = Parse(text.Substring(selected.Start, selected.Length)).FirstOrDefault(s => s.Start == 0 && s.Length == selected.Length && s.Ours == selected.Ours && s.Theirs == selected.Theirs && s.Base == selected.Base);
        if (actual is null) throw new InvalidOperationException("The conflict section changed. Select it again.");
        var replacement = choice switch { ConflictChoice.Ours => actual.Ours, ConflictChoice.Theirs => actual.Theirs, _ => actual.Ours + actual.Theirs };
        return text[..selected.Start] + replacement + text[(selected.Start + selected.Length)..];
    }
    private static bool Marker(ReadOnlySpan<char> text, char symbol)
    {
        var n = 0;
        while (n < text.Length && text[n] == symbol) n++;
        return n >= 7 && (n == text.Length || char.IsWhiteSpace(text[n]));
    }
}
