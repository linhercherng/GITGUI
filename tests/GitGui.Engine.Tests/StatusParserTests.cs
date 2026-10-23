using System.Text;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class StatusParserTests
{
    [Fact]
    public void Parses_ordinary_rename_conflict_and_untracked_records_without_losing_whitespace()
    {
        var data = "# branch.oid abc\0# branch.head main\0" +
            "1 .M N... 100644 100644 100644 abc abc 中文 file.txt\0" +
            "2 R. N... 100644 100644 100644 abc abc R100 new.txt\0old name.txt\0" +
            "u UU N... 100644 100644 100644 100644 a b c conflict.txt\0" +
            "? line\nbreak😀.txt\0";
        var result = PorcelainStatusParser.Parse(Encoding.UTF8.GetBytes(data));
        Assert.Equal("main", result.Branch);
        Assert.Equal(4, result.Files.Count);
        Assert.Equal("中文 file.txt", result.Files[0].Path);
        Assert.Equal("old name.txt", result.Files[1].OriginalPath);
        Assert.Equal("U", result.Files[2].IndexStatus);
        Assert.Equal("line\nbreak😀.txt", result.Files[3].Path);
    }

    [Fact]
    public void Retains_invalid_utf8_path_bytes()
    {
        byte[] data = [(byte)'?', (byte)' ', 0xff, 0];
        Assert.Equal(new byte[] { 0xff }, PorcelainStatusParser.Parse(data).Files[0].RawPath);
    }

    [Theory]
    [InlineData("1 broken\0")]
    [InlineData("2 R. N... 100644 100644 100644 a a R100 name\0")]
    [InlineData("? missing-terminator")]
    public void Rejects_truncated_output(string data)
        => Assert.Throws<FormatException>(() => PorcelainStatusParser.Parse(Encoding.UTF8.GetBytes(data)));
}
