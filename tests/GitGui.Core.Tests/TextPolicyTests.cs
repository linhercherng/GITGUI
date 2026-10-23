using System.Text;
using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public class TextPolicyTests
{
    [Fact]
    public void Headerless_Big5_is_detected_without_redecoding_other_commits()
    {
        var legacy = CommitMessageDecoder.Decode([0xa4, 0xa4, 0xa4, 0xe5, 10]);
        Assert.Equal("中文\n", legacy.Message); Assert.Equal(CommitEncodingSource.Big5Detected, legacy.Encoding.Source);
        var utf8 = CommitMessageDecoder.Decode(Encoding.UTF8.GetBytes("測試😀\n"));
        Assert.Equal("測試😀\n", utf8.Message); Assert.Equal(CommitEncodingSource.Utf8, utf8.Encoding.Source);
    }

    [Fact]
    public void Declared_encoding_is_authoritative_and_invalid_or_unknown_headers_are_marked()
    {
        var declared = CommitMessageDecoder.Decode([0xa4, 0xa4, 0xa4, 0xe5], "Big5");
        Assert.Equal("中文", declared.Message); Assert.Equal(CommitEncodingSource.Header, declared.Encoding.Source);
        var invalid = CommitMessageDecoder.Decode([0xff], "UTF-8");
        Assert.True(invalid.Encoding.IsLossy); Assert.Equal(CommitEncodingSource.Unreadable, invalid.Encoding.Source);
        var unknown = CommitMessageDecoder.Decode("valid"u8, "not-a-real-encoding");
        Assert.True(unknown.Encoding.IsLossy); Assert.Equal("not-a-real-encoding", unknown.Encoding.Name);
    }

    [Fact]
    public void Raw_commit_header_is_not_part_of_the_message_and_subject_matches_first_paragraph()
    {
        var raw = Encoding.ASCII.GetBytes("tree abc\nauthor User <x@y> 1 +0000\nencoding Big5\ngpgsig ignored\n continuation\n\n")
            .Concat(new byte[] { 0xa4, 0xa4, 0xa4, 0xe5, 10, 10, (byte)'b', (byte)'o', (byte)'d', (byte)'y', 10 }).ToArray();
        var result = CommitMessageDecoder.DecodeObject(raw);
        Assert.Equal("中文", result.Subject); Assert.Equal("中文\n\nbody\n", result.Message);
        Assert.Equal(CommitEncodingSource.Header, result.Encoding.Source);
    }

    [Fact]
    public void Invalid_path_bytes_and_literal_escape_names_have_distinct_displays()
    {
        var invalid = new FileChange(GitPathPolicy.DecodePath([0xff]), "?", "?", [0xff]);
        var replacement = new FileChange("�", "?", "?", Encoding.UTF8.GetBytes("�"));
        var literal = new FileChange("\\xFF", "?", "?", Encoding.UTF8.GetBytes("\\xFF"));
        Assert.False(invalid.IsPathSupported); Assert.True(replacement.IsPathSupported);
        Assert.Equal("\\xFF", invalid.DisplayPath); Assert.Equal("\\\\xFF", literal.DisplayPath);
        Assert.NotEqual(invalid.DisplayPath, replacement.DisplayPath);
        Assert.NotEqual(invalid.DisplayPath, literal.DisplayPath);
        Assert.Equal("line\\nbreak", GitPathPolicy.Display("line\nbreak"u8));
    }

    [Fact]
    public void Strict_message_encoding_refuses_unrepresentable_characters()
    {
        Assert.Equal(new byte[] { 0xa4, 0xa4, 0xa4, 0xe5 }, CommitMessageDecoder.Encode("中文", "Big5"));
        Assert.Throws<ArgumentException>(() => CommitMessageDecoder.Encode("😀", "Big5"));
        Assert.Throws<ArgumentException>(() => CommitMessageDecoder.Encode("text", "not-an-encoding"));
        Assert.Throws<ArgumentException>(() => CommitMessageDecoder.Encode("text", ""));
    }

    [Fact]
    public void Unsupported_destination_and_rename_source_cannot_be_selected_for_mutation()
    {
        FileChange invalid = new(GitPathPolicy.DecodePath([0xff]), "?", "?", [0xff]);
        FileChange rename = new("valid.txt", "R", ".", "valid.txt"u8.ToArray(), GitPathPolicy.DecodePath([0xfe]))
            { RawOriginalPath = [0xfe] };
        Assert.Throws<InvalidOperationException>(() => GitPathPolicy.ValidateSelection([invalid], [invalid.Path]));
        Assert.Throws<InvalidOperationException>(() => GitPathPolicy.ValidateSelection([rename], [rename.Path]));
        Assert.Throws<InvalidOperationException>(() => GitPathPolicy.ValidateSelection([], ["invalid\ud800"]));
        GitPathPolicy.ValidateSelection([invalid], ["�"]); // A valid UTF-8 alias is distinct from escaped raw FF.
    }
}
