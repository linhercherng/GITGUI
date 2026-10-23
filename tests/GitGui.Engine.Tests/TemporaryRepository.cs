using System.Diagnostics;
using System.Text;
using GitGui.Core;

namespace GitGui.Engine.Tests;

internal sealed class TemporaryRepository : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gitgui-m1-" + Guid.NewGuid().ToString("N"));
    public TemporaryRepository() => Directory.CreateDirectory(Root);

    public async Task<string> Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "missing-global-config");
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return await stdout;
    }

    public async Task Init(string format = "sha1", bool commit = true)
    {
        await Git("init", "-b", "main", "--object-format=" + format);
        await Git("config", "user.name", "GITGUI Test");
        await Git("config", "user.email", "test@example.invalid");
        await Git("config", "commit.gpgsign", "false");
        await Git("config", "core.autocrlf", "false");
        await Git("config", "core.hooksPath", Path.Combine(Root, ".git", "hooks"));
        if (!commit) return;
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked.txt"), "base\n");
        await Git("add", "--", "tracked.txt");
        await Git("commit", "-m", "initial");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(Root)) return;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Root, true);
    }
}

internal sealed class MemoryCatalog : ISnapshotCatalog
{
    private readonly List<SnapshotInfo> snapshots = [];
    public Task SaveAsync(SnapshotInfo snapshot, CancellationToken ct) { snapshots.Add(snapshot); return Task.CompletedTask; }
    public Task<IReadOnlyList<SnapshotInfo>> ListAsync(string root, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SnapshotInfo>>(snapshots.Where(s => s.Repository == root).OrderByDescending(s => s.CreatedAt).ToArray());
    public Task RemoveAsync(string id, CancellationToken ct) { snapshots.RemoveAll(s => s.Id == id); return Task.CompletedTask; }
}
