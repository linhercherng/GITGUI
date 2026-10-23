using System.Buffers;
using System.Text;

namespace GitGui.Core;

public enum CommitEncodingSource { Utf8, Header, Big5Detected, Unreadable }
public sealed record CommitMessageEncoding(string Name, CommitEncodingSource Source, bool IsLossy = false)
{
    public static readonly CommitMessageEncoding Utf8 = new("UTF-8", CommitEncodingSource.Utf8);
}
public sealed record CommitText(string Message, CommitMessageEncoding Encoding)
{
    public string Subject => string.Join(" ", Message.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('\n')
        .Split("\n\n", 2)[0].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())).TrimEnd();
}
public sealed record RepositoryTextSettings(string AutoCrlf = "unset", string SafeCrlf = "unset", string Eol = "unset",
    string CommitEncoding = "UTF-8")
{
    public string EffectiveAutoCrlf => AutoCrlf == "unset" ? "false" : AutoCrlf;
}

public static class CommitMessageDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static CommitMessageDecoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static Encoding Strict(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static CommitText Decode(ReadOnlySpan<byte> message, string? declaredEncoding = null)
    {
        if (declaredEncoding is not null)
        {
            try { return new(Strict(declaredEncoding).GetString(message), new(declaredEncoding, CommitEncodingSource.Header)); }
            catch (ArgumentException) { return Unreadable(message, declaredEncoding); }
        }
        try { return new(StrictUtf8.GetString(message), CommitMessageEncoding.Utf8); }
        catch (DecoderFallbackException)
        {
            try { return new(Strict("Big5").GetString(message), new("Big5", CommitEncodingSource.Big5Detected)); }
            catch (ArgumentException) { return Unreadable(message, "unknown"); }
        }
    }

    private static CommitText Unreadable(ReadOnlySpan<byte> message, string name)
        => new(Encoding.UTF8.GetString(message), new(name, CommitEncodingSource.Unreadable, true));

    public static CommitText DecodeObject(ReadOnlySpan<byte> commitObject)
    {
        var split = commitObject.IndexOf("\n\n"u8);
        if (split < 0) throw new FormatException("Commit object has no header/message separator.");
        string? encoding = null;
        foreach (var line in Encoding.ASCII.GetString(commitObject[..split]).Split('\n'))
            if (line.StartsWith("encoding ", StringComparison.Ordinal)) encoding = line[9..].Trim();
        return Decode(commitObject[(split + 2)..], encoding);
    }

    public static byte[] Encode(string message, string? configuredEncoding = null)
    {
        var name = configuredEncoding ?? "UTF-8";
        try { return Strict(name).GetBytes(message); }
        catch (ArgumentException error)
        {
            throw new ArgumentException($"Commit message cannot be encoded as {name}. Use representable characters or change i18n.commitEncoding explicitly before retrying.", nameof(message), error);
        }
    }
}

public static class GitPathPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static void ValidateSelection(IEnumerable<FileChange> changes, IReadOnlyList<string> paths)
    {
        var unsupported = changes.Where(c => !c.IsPathSupported).ToArray();
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var path in paths)
        {
            if (!IsRepresentable(path)) throw new InvalidOperationException("Unsupported repository path encoding. Rename this path with a byte-aware Git tool before editing it in GITGUI.");
            if (unsupported.Any(c => new[] { c.Path, c.OriginalPath }.OfType<string>().Any(p =>
                p.Equals(path, comparison) || p.StartsWith(path.TrimEnd('/') + "/", comparison))))
                throw new InvalidOperationException("This selection includes a non-UTF-8 repository path. Rename it with a byte-aware Git tool before editing it in GITGUI.");
        }
    }
    public static bool IsSupported(ReadOnlySpan<byte> path)
    {
        if (path.IsEmpty || path.Contains((byte)0)) return false;
        try { _ = StrictUtf8.GetCharCount(path); return true; }
        catch (DecoderFallbackException) { return false; }
    }
    public static bool IsRepresentable(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0')) return false;
        try { _ = StrictUtf8.GetByteCount(path); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    public static string DecodePath(ReadOnlySpan<byte> path) => IsSupported(path) ? StrictUtf8.GetString(path) : Display(path);
    public static string Display(string path) => Display(Encoding.UTF8.GetBytes(path));
    public static string Display(ReadOnlySpan<byte> path)
    {
        var text = new StringBuilder();
        while (!path.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(path, out var rune, out var consumed) != OperationStatus.Done)
            { text.Append("\\x").Append(path[0].ToString("X2")); path = path[1..]; continue; }
            var value = rune.Value;
            if (value == '\\') text.Append("\\\\");
            else if (value == '\n') text.Append("\\n");
            else if (value == '\r') text.Append("\\r");
            else if (value == '\t') text.Append("\\t");
            else if (Rune.IsControl(rune)) foreach (var b in path[..consumed]) text.Append("\\x").Append(b.ToString("X2"));
            else text.Append(rune.ToString());
            path = path[consumed..];
        }
        return text.ToString();
    }
}
