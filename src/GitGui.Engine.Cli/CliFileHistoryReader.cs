using System.Globalization;
using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed class CliFileHistoryReader(GitProcessRunner runner) : IFileHistoryReader
{
    public async Task<FileHistoryResult> ReadAsync(string root, string path, CancellationToken ct)
    {
        GitAccess.FullPath(root, path);
        var git = new GitAccess(runner);
        if (await git.TryText(root, ct, "rev-parse", "--verify", "--quiet", "HEAD") is null)
            return new([], [], "Git CLI");
        var blame = new List<BlameLine>();
        var present = (await git.Bytes(root, ct, ["ls-tree", "-z", "HEAD", "--", path])).Length > 0;
        var text = present ? await git.Text(root, ct, "blame", "--line-porcelain", "HEAD", "--", path) : "";
        string oid = "", author = ""; var number = 0;
        foreach (var line in text.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();
            if (line.StartsWith('\t')) blame.Add(new(number, oid, author, line[1..]));
            else if (line.StartsWith("author ", StringComparison.Ordinal)) author = line[7..];
            else
            {
                var fields = line.Split(' ');
                if (fields.Length >= 3 && fields[0].Length is 40 or 64 && int.TryParse(fields[2], out number)) oid = fields[0];
            }
        }
        var log = (await git.Text(root, ct, "log", "--follow", "--topo-order", "--encoding=UTF-8",
            "--format=%H%x00%s%x00%an%x00%aI%x00%P", "--name-status", "-z", "HEAD", "--", path)).Split('\0');
        var history = new List<FileHistoryEntry>();
        var currentPath = path;
        for (var n = 0; n < log.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (log[n].Length == 0) { n++; continue; }
            if (n + 4 >= log.Length) throw new FormatException("Incomplete file history record.");
            var hash = log[n++]; var subject = log[n++]; var name = log[n++]; var date = log[n++];
            var parents = log[n++].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            history.Add(new(new(hash, subject, name, DateTimeOffset.Parse(date, CultureInfo.InvariantCulture), parents), currentPath));
            // NUL-delimited names may contain newlines; trim only the status separator.
            while (n < log.Length)
            {
                var kind = log[n].TrimStart('\r', '\n');
                if (kind.Length == 0) { n++; continue; }
                if (kind.Length is 40 or 64 && kind.All(Uri.IsHexDigit)) break;
                n++;
                if (n >= log.Length || kind[0] is not ('A' or 'C' or 'D' or 'M' or 'R' or 'T' or 'U' or 'X' or 'B'))
                    throw new FormatException("Invalid file history change record.");
                var from = log[n++];
                if (kind.StartsWith('R') || kind.StartsWith('C'))
                {
                    if (n >= log.Length) throw new FormatException("Incomplete file history rename record.");
                    var to = log[n++];
                    if (kind.StartsWith('R') && to == currentPath) currentPath = from;
                }
            }
        }
        return new(blame, history, "Git CLI");
    }
}
