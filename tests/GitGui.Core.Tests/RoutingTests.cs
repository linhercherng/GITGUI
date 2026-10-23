using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public class RoutingTests
{
    [Theory]
    [InlineData("sha1", "files", true)]
    [InlineData("sha256", "files", false)]
    [InlineData("sha1", "reftable", false)]
    public void Library_is_only_used_for_supported_formats(string objects, string refs, bool expected)
        => Assert.Equal(expected, ReadBackendPolicy.CanUseLibrary(new(objects, refs)));

    [Fact]
    public void Core_has_no_external_or_process_dependencies()
    {
        var assembly = typeof(RepoStatus).Assembly;
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("Avalonia") || a.Name.StartsWith("LibGit2Sharp") ||
            a.Name == "System.Diagnostics.Process");
    }
}
