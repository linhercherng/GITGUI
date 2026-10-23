using System.Globalization;
using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed class CliRepoReader(GitProcessRunner runner) : IRepoReader
{
    private async Task<byte[]> Run(string repository, CancellationToken ct, params string[] args)
    {
        var result = await runner.ReadAsync(repository, args, ct);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
        return result.Output;
    }

    public Task<RepoStatus> GetStatusAsync(string repository, CancellationToken ct)
        => GetStatusAsync(repository, false, ct);

    public async Task<RepoStatus> GetStatusAsync(string repository, bool trusted, CancellationToken ct)
    {
        var version = Encoding.UTF8.GetString(await Run(repository, ct, "--version")).Split(' ')[2];
        var digits = version.Split('.');
        if (int.Parse(digits[0], CultureInfo.InvariantCulture) < 2 ||
            (digits[0] == "2" && int.Parse(digits[1], CultureInfo.InvariantCulture) < 40))
            throw new InvalidOperationException("Git 2.40 or newer is required.");
        var result = await runner.RunAsync(repository,
            ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], ct, new() { Trusted = trusted });
        if (result.ExitCode != 0) throw new GitCommandException(result);
        return PorcelainStatusParser.Parse(result.Output);
    }

    public async Task<RepoFormat> GetFormatAsync(string repository, CancellationToken ct)
    {
        async Task<string> Config(string key, string fallback)
        {
            var result = await runner.ReadAsync(repository, ["config", "--get", key], ct);
            if (result.ExitCode == 1) return fallback;
            if (result.ExitCode != 0) throw new InvalidOperationException(result.Error.Trim());
            return Encoding.UTF8.GetString(result.Output).Trim();
        }
        return new(await Config("extensions.objectFormat", "sha1"), await Config("extensions.refStorage", "files"));
    }

    public async Task<IReadOnlyList<CommitInfo>> GetLogAsync(string repository, int limit, CancellationToken ct)
    {
        var status = await GetStatusAsync(repository, ct);
        if (status.Head == "(initial)") return [];
        // Fixed-size NUL fields: commit subjects may contain control characters, never split on newlines.
        var bytes = await Run(repository, ct, "log", "--topo-order", "-z",
            "-n", Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture),
            "--format=%H%x00%s%x00%an%x00%aI%x00%P");
        var fields = Encoding.UTF8.GetString(bytes).Split('\0');
        var commits = new List<CommitInfo>();
        for (var n = 0; n + 4 < fields.Length; n += 5)
            commits.Add(new(fields[n], fields[n + 1], fields[n + 2],
                DateTimeOffset.Parse(fields[n + 3], CultureInfo.InvariantCulture),
                fields[n + 4].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        return commits;
    }

    public async Task<string> GetDiffAsync(string repository, string path, bool staged, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var args = new List<string> { "diff", "--no-color", "--no-ext-diff", "--no-textconv" };
        if (staged) args.Add("--cached");
        args.Add("--");
        args.Add(path);
        var output = await Run(repository, ct, args.ToArray());
        return output.Length == 0 ? "No tracked diff. Untracked files are listed in status only." : Encoding.UTF8.GetString(output);
    }
}
