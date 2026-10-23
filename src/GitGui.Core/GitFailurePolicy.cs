using System.Text.RegularExpressions;

namespace GitGui.Core;

public enum GitSuggestedAction { None, Fetch, Pull, ResolveConflicts, CredentialsHelp, LockHelp, ShowOutput, Retry, Stash, Prune }

public static class GitFailurePolicy
{
    public static GitSuggestedAction Suggest(GitErrorKind error) => error switch
    {
        GitErrorKind.None => GitSuggestedAction.None,
        GitErrorKind.StaleLease => GitSuggestedAction.Fetch,
        GitErrorKind.NonFastForward => GitSuggestedAction.Pull,
        GitErrorKind.Conflict => GitSuggestedAction.ResolveConflicts,
        GitErrorKind.AuthFailed => GitSuggestedAction.CredentialsHelp,
        GitErrorKind.LockExists => GitSuggestedAction.LockHelp,
        GitErrorKind.Network => GitSuggestedAction.Retry,
        GitErrorKind.DirtyWorktree => GitSuggestedAction.Stash,
        GitErrorKind.WorktreeCleanupIncomplete => GitSuggestedAction.Prune,
        _ => GitSuggestedAction.ShowOutput
    };

    public static GitErrorKind Classify(int exitCode, string text, bool hasUnmergedEntries = false)
    {
        if (exitCode == 0) return GitErrorKind.None;
        if (text.Length > 64 * 1024) text = text[..(64 * 1024)];
        bool Has(string pattern)
        {
            try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (RegexMatchTimeoutException) { return false; }
        }
        if (Has("stale info")) return GitErrorKind.StaleLease;
        if (Has(@"Authentication failed|Permission denied \(publickey\)|could not read Username")) return GitErrorKind.AuthFailed;
        if (Has(@"index\.lock exists; wait|Unable to create [^\r\n]*\.lock[^\r\n]*File exists|another git process seems to be running|cannot lock ref[^\r\n]*\.lock")) return GitErrorKind.LockExists;
        if (Has(@"(?:pre-commit|pre-push|pre-receive|pre-rebase|commit-msg|update) hook (?:rejected|failed|declined)|hook declined")) return GitErrorKind.HookRejected;
        if (Has(@"non-fast-forward|Not possible to fast-forward|\[rejected\]")) return GitErrorKind.NonFastForward;
        if (Has(@"Could not resolve host|Could not resolve proxy|timed out|Failed to connect|Connection reset by peer")) return GitErrorKind.Network;
        if (Has(@"would be overwritten|local changes.*would be lost|unstaged changes|uncommitted changes")) return GitErrorKind.DirtyWorktree;
        if (hasUnmergedEntries && exitCode == 1) return GitErrorKind.Conflict;
        return GitErrorKind.Unknown;
    }
}
