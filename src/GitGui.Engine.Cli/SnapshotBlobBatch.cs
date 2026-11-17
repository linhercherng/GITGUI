using System.Text;

namespace GitGui.Engine.Cli;

// Spool captured bytes so Git hashes exactly the state we inspected, in one process.
internal sealed class SnapshotBlobBatch : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "gitgui-blobs-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> paths = [];
    public async Task<int> AddAsync(byte[] content, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var slot = paths.Count;
        var path = Path.Combine(directory, slot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        paths.Add(path);
        await File.WriteAllBytesAsync(path, content, ct);
        return slot;
    }
    public async Task<string[]> WriteAsync(GitAccess git, string root, CancellationToken ct)
    {
        if (paths.Count == 0) return [];
        var output = await git.Bytes(root, ct, ["hash-object", "-w", "--no-filters", "--stdin-paths"],
            new() { IsReadOnly = false, HideOutput = true,
                Input = Encoding.UTF8.GetBytes(string.Join("\n", paths.Select(GitAccess.QuotePath)) + "\n") });
        var oids = GitAccess.Utf8.GetString(output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        if (oids.Length != paths.Count || oids.Any(s => s.Length is not (40 or 64) || s.Any(c => !char.IsAsciiHexDigit(c))))
            throw new IOException("Snapshot object count or hash mismatch.");
        return oids;
    }
    public void Dispose()
    {
        foreach (var path in paths) File.Delete(path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}
