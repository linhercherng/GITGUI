namespace GitGui.Core;

public enum ConflictChoice { Ours, Theirs, Both }
public sealed record ConflictSection(int Id, int Start, int Length, string Ours, string Theirs, string? Base)
{
    public string Label => $"{Id + 1} · {Ours.Split('\n')[0].TrimEnd('\r')}";
}

/// <summary>Parses Git's normal and diff3 markers while preserving original newline bytes.</summary>
public static class ConflictMarkers
{
    private sealed record Line(int Start, string Text, int End);
    public static IReadOnlyList<ConflictSection> Parse(string text)
    {
        var lines = new List<Line>();
        for (var start = 0; start < text.Length;)
        {
            var next = text.IndexOf('\n', start);
            var end = next < 0 ? text.Length : next + 1;
            lines.Add(new(start, text[start..end], end)); start = end;
        }
        var sections = new List<ConflictSection>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Marker(lines[i].Text, '<')) continue;
            var begin = i; var baseAt = -1; var split = -1; var endAt = -1;
            for (var j = i + 1; j < lines.Count; j++)
            {
                if (Marker(lines[j].Text, '<')) break;
                if (Marker(lines[j].Text, '|') && split < 0) baseAt = j;
                else if (Marker(lines[j].Text, '=') && split < 0) split = j;
                else if (Marker(lines[j].Text, '>') && split >= 0) { endAt = j; break; }
            }
            if (split < 0 || endAt < 0) continue;
            var oursEnd = baseAt < 0 ? lines[split].Start : lines[baseAt].Start;
            var ours = text[lines[begin].End..oursEnd];
            var theirs = text[lines[split].End..lines[endAt].Start];
            var ancestor = baseAt < 0 ? null : text[lines[baseAt].End..lines[split].Start];
            sections.Add(new(sections.Count, lines[begin].Start, lines[endAt].End - lines[begin].Start, ours, theirs, ancestor));
            i = endAt;
        }
        return sections;
    }
    public static string Replace(string text, ConflictSection selected, ConflictChoice choice)
    {
        var actual = Parse(text).FirstOrDefault(s => s.Start == selected.Start && s.Length == selected.Length && s.Ours == selected.Ours && s.Theirs == selected.Theirs);
        if (actual is null) throw new InvalidOperationException("The conflict section changed. Select it again.");
        var replacement = choice switch { ConflictChoice.Ours => actual.Ours, ConflictChoice.Theirs => actual.Theirs, _ => actual.Ours + actual.Theirs };
        return text[..actual.Start] + replacement + text[(actual.Start + actual.Length)..];
    }
    private static bool Marker(string text, char symbol)
    {
        var n = 0;
        while (n < text.Length && text[n] == symbol) n++;
        return n >= 7 && (n == text.Length || char.IsWhiteSpace(text[n]));
    }
}
