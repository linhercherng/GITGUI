using GitGui.Core;
using Xunit;

namespace GitGui.Core.Tests;

public class GitFailurePolicyTests
{
    [Theory]
    [InlineData("fatal: Authentication failed", GitErrorKind.AuthFailed)]
    [InlineData("fatal: Unable to create '.git/index.lock': File exists", GitErrorKind.LockExists)]
    [InlineData("fatal: Could not resolve host: example.invalid", GitErrorKind.Network)]
    [InlineData("pre-commit hook rejected", GitErrorKind.HookRejected)]
    [InlineData("error: unknown operation failure", GitErrorKind.Conflict)]
    public void Explicit_command_failure_precedes_existing_unmerged_index(string stderr, GitErrorKind expected)
        => Assert.Equal(expected, GitFailurePolicy.Classify(1, stderr, hasUnmergedEntries: true));

    [Fact]
    public void Incidental_filename_words_do_not_classify_as_hook_or_lock_failures()
    {
        Assert.Equal(GitErrorKind.Unknown, GitFailurePolicy.Classify(128, "fatal: repository 'hook/index.lock' does not exist"));
        Assert.Equal(GitErrorKind.None, GitFailurePolicy.Classify(0, "Authentication failed"));
    }
}
