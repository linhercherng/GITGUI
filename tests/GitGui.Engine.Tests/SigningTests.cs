using System.Diagnostics;
using GitGui.Core;
using GitGui.Engine.Cli;
using Xunit;

namespace GitGui.Engine.Tests;

public class SigningTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_unsigned_commit_overrides_config_for_one_invocation_only(bool configured)
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "commit.gpgsign", configured ? "true" : "false");
        await repo.Git("config", "gpg.program", "gitgui-missing-signer");
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "unsigned\n"); await repo.Git("add", ".");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var commands = new List<CommandEntry>(); runner.CommandExecuted += (_, entry) => commands.Add(entry);
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var state = await service.OpenAsync(repo.Root, new(), default);
        Assert.Equal(configured ? "true" : "false", state.TextSettings.CommitSigning);
        var invalid = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Sign = true, DisableSigning = true, Message = "invalid" }, null, default);
        Assert.False(invalid.Success); Assert.Empty(await catalog.ListAsync(repo.Root, default));
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { DisableSigning = true, Message = "explicit unsigned" }, null, default);
        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("gpgsig ", await repo.Git("cat-file", "commit", "HEAD"));
        Assert.Equal(configured ? "true" : "false", (await repo.Git("config", "commit.gpgsign")).Trim());
        var command = Assert.Single(commands, c => c.Arguments.Contains("commit"));
        Assert.Contains("--no-gpg-sign", command.Arguments); Assert.DoesNotContain("--no-verify", command.Arguments);
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "sign\n"); await repo.Git("add", ".");
        var signed = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Sign = true, Message = "must sign" }, null, default);
        Assert.False(signed.Success); Assert.Equal(GitErrorKind.SigningFailed, signed.Error);
        Assert.Equal(configured ? "true" : "false", (await repo.Git("config", "commit.gpgsign")).Trim());
    }

    [Fact]
    public async Task Inherited_signing_failure_is_identified_without_changing_config_or_losing_staged_changes()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "commit.gpgsign", "true");
        await repo.Git("config", "gpg.program", "gitgui-missing-signer");
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "sign me\n");
        await repo.Git("add", ".");
        var before = (await repo.Git("rev-parse", "HEAD")).Trim();
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Message = "signed by config" }, null, default);
        Assert.False(result.Success);
        Assert.Equal("SigningFailed", result.Error.ToString());
        Assert.Equal(before, (await repo.Git("rev-parse", "HEAD")).Trim());
        Assert.Equal("true", (await repo.Git("config", "commit.gpgsign")).Trim());
        Assert.Contains("tracked.txt", await repo.Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Unchecked_tag_signing_is_explicit_and_never_starts_an_editor()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        await repo.Git("config", "tag.gpgsign", "true"); await repo.Git("config", "core.editor", "false");
        await repo.Git("config", "gpg.program", "gitgui-missing-signer");
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        var lightweight = await service.ExecuteAsync(repo.Root, new(GitAction.CreateTag) { Name = "unsigned-lightweight" }, null, default);
        Assert.True(lightweight.Success, lightweight.Message);
        Assert.Equal("commit", (await repo.Git("cat-file", "-t", "unsigned-lightweight")).Trim());
        var annotated = await service.ExecuteAsync(repo.Root, new(GitAction.CreateTag) { Name = "unsigned-annotated", Annotated = true, Message = "unsigned" }, null, default);
        Assert.True(annotated.Success, annotated.Message);
        Assert.DoesNotContain("SIGNATURE", await repo.Git("cat-file", "-p", "unsigned-annotated"));
    }

    [Fact]
    public async Task Configured_ssh_signer_creates_verifiable_commit_and_tag_then_snapshot_undo()
    {
        using var repo = new TemporaryRepository(); await repo.Init();
        var key = Path.Combine(repo.Root, ".git", "signing key");
        var start = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", "", "-f", key }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!)
        { var error = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); Assert.True(process.ExitCode == 0, await error); }
        var allowed = Path.Combine(repo.Root, ".git", "allowed-signers");
        await File.WriteAllTextAsync(allowed, "test@example.invalid " + await File.ReadAllTextAsync(key + ".pub"));
        await repo.Git("config", "gpg.format", "ssh"); await repo.Git("config", "user.signingkey", key);
        await repo.Git("config", "gpg.ssh.allowedSignersFile", allowed);
        var runner = new GitProcessRunner(); var catalog = new MemoryCatalog();
        var service = new WorkspaceService(runner, new TestSettings(), catalog, new(runner, catalog), new TestInteraction());
        await File.AppendAllTextAsync(Path.Combine(repo.Root, "tracked.txt"), "signed\n"); await repo.Git("add", ".");
        var commit = await service.ExecuteAsync(repo.Root, new(GitAction.Commit) { Sign = true, Message = "signed commit" }, null, default);
        Assert.True(commit.Success, commit.Message); await repo.Git("verify-commit", "HEAD");
        var result = await service.ExecuteAsync(repo.Root, new(GitAction.CreateTag) { Name = "signed-tag", Sign = true, Message = "signed tag" }, null, default);
        Assert.True(result.Success, result.Message);
        Assert.Contains("BEGIN SSH SIGNATURE", await repo.Git("cat-file", "-p", "signed-tag"));
        await repo.Git("verify-tag", "signed-tag");
        var undo = await service.ExecuteAsync(repo.Root, new(GitAction.RestoreSnapshot) { SnapshotId = result.Snapshot!.Id }, null, default);
        Assert.True(undo.Success, undo.Message); Assert.Empty((await repo.Git("tag", "--list", "signed-tag")).Trim());
    }
}
