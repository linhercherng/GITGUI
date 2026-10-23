namespace GitGui.Core;

public sealed record NativeReadContext(IReadOnlyList<KeyValuePair<string, string>> Configuration,
    string GitExecutable, string? HomeDirectory, string? XdgConfigHome, bool SystemAttributesEnabled);

/// <summary>Returns null when native semantics are unsupported; callers then use the selected Git CLI.</summary>
public interface INativeReadBackend
{
    Task<IReadOnlyList<RefInfo>?> TryReadRefsAsync(string root, NativeReadContext context, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<RefInfo>?>(null);
    Task<string?> TryReadDiffAsync(string root, DiffRequest request, NativeReadContext context, CancellationToken ct);
    Task<FilePreview?> TryReadPreviewAsync(string root, DiffRequest request, NativeReadContext context, CancellationToken ct);
}
