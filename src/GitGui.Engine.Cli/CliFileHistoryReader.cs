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
        var log = await git.Text(root, ct, "log", "--follow", "--max-count=500", "--format=%H", "HEAD", "--", path);
        var history = new List<FileHistoryEntry>();
        var currentPath = path;
        foreach (var hash in log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.TrimEnd('\r')))
        {
            var fields = (await git.Text(root, ct, "show", "-s", "--encoding=UTF-8", "--format=%H%x00%s%x00%an%x00%aI%x00%P", hash)).Split('\0');
            var parents = fields[4].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            history.Add(new(new(fields[0], fields[1], fields[2], DateTimeOffset.Parse(fields[3], CultureInfo.InvariantCulture), parents), currentPath));
            if (parents.Length == 0) continue;
            var changes = GitAccess.NullFields(await git.Bytes(root, ct, ["diff-tree", "-r", "-M", "--name-status", "-z", parents[0], hash, "--"]));
            for (var n = 0; n < changes.Length;)
            {
                var kind = changes[n++]; var from = changes[n++];
                if (kind.StartsWith('R') || kind.StartsWith('C'))
                {
                    var to = changes[n++];
                    if (kind.StartsWith('R') && to == currentPath) currentPath = from;
                }
            }
        }
        return new(blame, history, "Git CLI");
    }
}
