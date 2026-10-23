using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GitGui.Core;

public sealed record PatchLine(int Id, char Kind, string Text, int? OldLine, int? NewLine)
{
    public bool IsChange => Kind is '+' or '-';
}
public sealed record PatchHunk(int Id, string Header, IReadOnlyList<PatchLine> Lines);

public static partial class UnifiedPatch
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HeaderPattern();

    public static IReadOnlyList<PatchHunk> Parse(string patch)
    {
        var result = new List<PatchHunk>();
        var source = patch.Split('\n');
        List<PatchLine>? current = null;
        var oldLine = 0;
        var newLine = 0;
        for (var n = 0; n < source.Length; n++)
        {
            var text = source[n];
            var match = HeaderPattern().Match(text);
            if (match.Success)
            {
                oldLine = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                newLine = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                current = [];
                result.Add(new(result.Count, text, current));
                continue;
            }
            if (text.StartsWith("diff --git ", StringComparison.Ordinal)) { current = null; continue; }
            if (current is null || text.Length == 0 || text[0] is not (' ' or '+' or '-' or '\\')) continue;
            var kind = text[0];
            current.Add(new(n, kind, text[1..], kind is ' ' or '-' ? oldLine : null, kind is ' ' or '+' ? newLine : null));
            if (kind is ' ' or '-') oldLine++;
            if (kind is ' ' or '+') newLine++;
        }
        return result;
    }

    // For unstage, reverse BEFORE selecting: the current index is the patch base.
    public static string Build(string patch, IReadOnlySet<int> selectedHunks, IReadOnlySet<int> selectedLines, bool reverse = false)
    {
        var hunks = Parse(patch);
        if (hunks.Count == 0) throw new InvalidOperationException("This diff has no selectable text hunks.");
        var source = patch.Split('\n');
        if (source.Count(s => s.StartsWith("diff --git ", StringComparison.Ordinal)) > 1)
            throw new InvalidOperationException("Select a single file before staging a partial patch.");
        var header = source.TakeWhile(s => !HeaderPattern().IsMatch(s)).ToList();
        var output = new StringBuilder();
        var delta = 0;
        var emitted = false;
        bool Selected(PatchHunk h, PatchLine l) => selectedHunks.Contains(h.Id) || selectedLines.Contains(l.Id);
        var allChangesSelected = hunks.All(h => h.Lines.Where(l => l.IsChange).All(l => Selected(h, l)));
        var originalOld = header.FirstOrDefault(s => s.StartsWith("--- ", StringComparison.Ordinal))
            ?? throw new FormatException("Missing old-file header.");
        var originalNew = header.FirstOrDefault(s => s.StartsWith("+++ ", StringComparison.Ordinal))
            ?? throw new FormatException("Missing new-file header.");
        var oldPath = (reverse ? originalNew : originalOld)[4..];
        var newPath = (reverse ? originalOld : originalNew)[4..];
        var fileMode = header.FirstOrDefault(s => s.StartsWith("new file mode ", StringComparison.Ordinal) ||
            s.StartsWith("deleted file mode ", StringComparison.Ordinal))?.Split(' ')[^1] ?? "100644";
        // Partial deletion must keep the remaining file, including reverse of a new-file diff.
        if (newPath == "/dev/null" && !allChangesSelected)
            newPath = oldPath.StartsWith("\"", StringComparison.Ordinal) ? oldPath.Replace("\"a/", "\"b/", StringComparison.Ordinal)
                .Replace("\"b/", "\"b/", StringComparison.Ordinal) : "b/" + oldPath[2..];
        foreach (var line in header)
        {
            if (line.StartsWith("index ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal) ||
                line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("new file mode ", StringComparison.Ordinal) ||
                line.StartsWith("deleted file mode ", StringComparison.Ordinal) || line.StartsWith("old mode ", StringComparison.Ordinal) ||
                line.StartsWith("new mode ", StringComparison.Ordinal)) continue;
            output.Append(line).Append('\n');
        }
        // A selected fragment must not implicitly stage an unrelated chmod. A whole file can include it.
        if (allChangesSelected)
        {
            var oldMode = header.FirstOrDefault(s => s.StartsWith("old mode ", StringComparison.Ordinal))?[9..];
            var newMode = header.FirstOrDefault(s => s.StartsWith("new mode ", StringComparison.Ordinal))?[9..];
            if (oldMode is not null && newMode is not null)
            {
                output.Append("old mode ").Append(reverse ? newMode : oldMode).Append('\n');
                output.Append("new mode ").Append(reverse ? oldMode : newMode).Append('\n');
            }
        }
        if (oldPath == "/dev/null") output.Append("new file mode ").Append(fileMode).Append('\n');
        if (newPath == "/dev/null") output.Append("deleted file mode ").Append(fileMode).Append('\n');
        output.Append("--- ").Append(oldPath).Append("\n+++ ").Append(newPath).Append('\n');
        foreach (var hunk in hunks)
        {
            if (!hunk.Lines.Any(l => l.IsChange && Selected(hunk, l))) continue;
            var body = new List<string>();
            var previousIncluded = false;
            foreach (var line in hunk.Lines)
            {
                var kind = reverse ? line.Kind switch { '+' => '-', '-' => '+', _ => line.Kind } : line.Kind;
                if (kind == '\\') { if (previousIncluded) body.Add("\\" + line.Text); continue; }
                if (kind == '+' && !Selected(hunk, line)) { previousIncluded = false; continue; }
                if (kind == '-' && !Selected(hunk, line)) kind = ' ';
                body.Add(kind + line.Text);
                previousIncluded = true;
            }
            var oldCount = body.Count(l => l[0] is ' ' or '-');
            var newCount = body.Count(l => l[0] is ' ' or '+');
            var match = HeaderPattern().Match(hunk.Header);
            var oldStart = int.Parse(match.Groups[reverse ? 3 : 1].Value, CultureInfo.InvariantCulture);
            var originalCount = match.Groups[reverse ? 4 : 2];
            var baseCount = originalCount.Success ? int.Parse(originalCount.Value, CultureInfo.InvariantCulture) : 1;
            var newStart = oldStart + delta + (baseCount == 0 && newCount > 0 ? 1 : 0);
            if (newCount == 0) newStart = Math.Max(0, oldStart + delta - 1);
            output.Append(CultureInfo.InvariantCulture, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            foreach (var line in body) output.Append(line).Append('\n');
            delta += newCount - oldCount;
            emitted = true;
        }
        if (!emitted) throw new InvalidOperationException("Select at least one changed hunk or line.");
        return output.ToString();
    }
}
