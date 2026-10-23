using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Cli;

// Format: https://git-scm.com/docs/git-status#_porcelain_format_version_2
public static class PorcelainStatusParser
{
    public static RepoStatus Parse(byte[] data)
    {
        var files = new List<FileChange>();
        var branch = "(detached)";
        string? head = null;
        var offset = 0;
        byte[] Next()
        {
            var end = Array.IndexOf(data, (byte)0, offset);
            if (end < 0) throw new FormatException("Truncated porcelain record.");
            var record = data[offset..end];
            offset = end + 1;
            return record;
        }
        while (offset < data.Length)
        {
            var record = Next();
            if (record.Length == 0) continue;
            var text = Encoding.UTF8.GetString(record);
            if (text.StartsWith("# branch.head ")) { branch = text[14..]; continue; }
            if (text.StartsWith("# branch.oid ")) { head = text[13..]; continue; }
            if (record[0] == '#') continue;
            var fields = record[0] switch { (byte)'1' => 8, (byte)'2' => 9,
                (byte)'u' => 10, (byte)'?' or (byte)'!' => 1,
                _ => throw new FormatException("Unknown porcelain record.") };
            var pathOffset = 0;
            for (var n = 0; n < fields; n++)
            {
                pathOffset = Array.IndexOf(record, (byte)' ', pathOffset);
                if (pathOffset < 0) throw new FormatException("Invalid porcelain fields.");
                pathOffset++;
            }
            var path = record[pathOffset..];
            if (path.Length == 0) throw new FormatException("Empty porcelain path.");
            var xy = fields == 1 ? "??" : text.Split(' ', 3)[1];
            if (xy.Length != 2) throw new FormatException("Invalid porcelain status.");
            string? original = record[0] == '2' ? Encoding.UTF8.GetString(Next()) : null;
            files.Add(new(Encoding.UTF8.GetString(path), xy[..1], xy[1..], path, original));
        }
        return new(branch, head, files);
    }
}
