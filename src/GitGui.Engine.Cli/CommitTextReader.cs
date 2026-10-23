using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using GitGui.Core;

namespace GitGui.Engine.Cli;

/// <summary>Reads original object bytes, independently of Git's log output transcoding.</summary>
public sealed class CommitTextReader(GitProcessRunner runner)
{
    public async Task<CommitText> ReadAsync(string root, string fullOid, CancellationToken ct)
        => (await ReadMany(root, [fullOid], ct))[0];

    public async Task<IReadOnlyList<CommitInfo>> EnrichAsync(string root, IReadOnlyList<CommitInfo> commits, CancellationToken ct)
    {
        if (commits.Count == 0) return commits;
        var messages = await ReadMany(root, commits.Select(c => c.Hash).ToArray(), ct);
        return commits.Select((commit, i) =>
        {
            var subject = messages[i].Subject; var encoding = messages[i].Encoding;
            return commit.Subject == subject && commit.MessageEncoding == encoding
                ? commit : commit with { Subject = subject, MessageEncoding = encoding };
        }).ToArray();
    }

    private async Task<IReadOnlyList<CommitText>> ReadMany(string root, IReadOnlyList<string> hashes, CancellationToken ct)
    {
        if (hashes.Any(h => !Regex.IsMatch(h, "^[0-9a-f]{40}([0-9a-f]{24})?$")))
            throw new ArgumentException("Commit text requires a complete object ID.", nameof(hashes));
        var result = await runner.RunAsync(root, ["cat-file", "--batch"], ct, new()
        { HideOutput = true, Input = Encoding.ASCII.GetBytes(string.Join("\n", hashes) + "\n") });
        if (result.ExitCode != 0) throw new GitCommandException(result);
        var messages = new List<CommitText>(); var offset = 0;
        foreach (var hash in hashes)
        {
            ct.ThrowIfCancellationRequested();
            var end = Array.IndexOf(result.Output, (byte)'\n', offset);
            if (end < 0) throw new FormatException("Truncated cat-file object header.");
            var fields = Encoding.ASCII.GetString(result.Output, offset, end - offset).Split(' ');
            if (fields.Length != 3 || fields[0] != hash || fields[1] != "commit" ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 ||
                length > result.Output.Length - end - 2)
                throw new FormatException("Invalid or missing commit object in cat-file response.");
            offset = end + 1;
            messages.Add(CommitMessageDecoder.DecodeObject(result.Output.AsSpan(offset, length)));
            offset += length;
            if (result.Output[offset++] != '\n') throw new FormatException("Truncated cat-file object separator.");
        }
        if (offset != result.Output.Length) throw new FormatException("Unexpected cat-file response data.");
        return messages;
    }
}
