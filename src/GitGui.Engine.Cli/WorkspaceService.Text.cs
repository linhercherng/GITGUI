using GitGui.Core;

namespace GitGui.Engine.Cli;

public sealed partial class WorkspaceService
{
    private sealed record OperationMessageBytes(byte[]? Commit, string Encoding, IReadOnlyDictionary<string, byte[]> Rebase);

    // Validate every app-authored message before creating snapshot refs or mutating the index/worktree.
    private async Task<OperationMessageBytes> PrepareMessageBytes(string root, GitOperation op, CancellationToken ct)
    {
        if (op.Action is not (GitAction.Commit or GitAction.RebaseInteractive or GitAction.ContinueOperation or GitAction.SkipOperation))
            return new(null, "UTF-8", new Dictionary<string, byte[]>());
        var encoding = (await new TextConfigurationReader(runner).ReadAsync(root, ct)).CommitEncoding;
        var messages = new Dictionary<string, byte[]>();
        byte[]? commit = op.Action == GitAction.Commit ? CommitMessageDecoder.Encode(op.Message!, encoding) : null;
        if (op.Action == GitAction.RebaseInteractive)
        {
            string? squashMessage = null;
            foreach (var step in op.RebasePlan)
            {
                if (step.Action == RebaseStepKind.Drop) continue;
                if (step.Action == RebaseStepKind.Squash && !string.IsNullOrWhiteSpace(step.Message)) squashMessage = step.Message;
                else if (step.Action is not (RebaseStepKind.Fixup or RebaseStepKind.Squash)) squashMessage = null;
                var message = step.Action == RebaseStepKind.Reword ? step.Message : squashMessage;
                if (!string.IsNullOrWhiteSpace(message)) messages[step.Commit] = CommitMessageDecoder.Encode(message.TrimEnd() + "\n", encoding);
            }
        }
        if (op.Action is GitAction.ContinueOperation or GitAction.SkipOperation &&
            Directory.Exists(Path.Combine(await git.Text(root, ct, "rev-parse", "--absolute-git-dir"), "rebase-merge")))
        {
            var file = Path.Combine(await RebaseDirectory(root, ct), "message-encoding");
            if (File.Exists(file))
            {
                if (new FileInfo(file).LinkTarget is not null) throw new InvalidOperationException("Unsafe rebase helper file.");
                if (!(await File.ReadAllTextAsync(file, ct)).Equals(encoding, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("i18n.commitEncoding changed during rebase. Restore its original value before continuing so saved message bytes match their commit headers.");
            }
        }
        return new(commit, encoding, messages);
    }
}
