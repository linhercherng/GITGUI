using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public class GitVersionTests
{
    [Theory]
    [InlineData("git version 2.39.5", false)]
    [InlineData("git version 1.99.0", false)]
    [InlineData("git version 2.40.0.windows.1", true)]
    [InlineData("git version 2.56.0.windows.3", true)]
    [InlineData("git version 3.0.0", true)]
    [InlineData("not git", false)]
    public void Minimum_supported_Git_is_2_40(string output, bool supported)
        => Assert.Equal(supported, GitVersionPolicy.Accepts(output));
}
