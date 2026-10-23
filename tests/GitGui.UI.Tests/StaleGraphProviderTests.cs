using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GitGui.Controls;
using GitGui.Core;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class StaleGraphProviderTests
{
    [AvaloniaFact]
    public async Task Held_row_provider_resolves_reloaded_commit_by_hash_and_ignores_removed_rows()
    {
        var hash = new string('a', 40);
        var original = new CommitInfo(hash, "Original", "Author", DateTimeOffset.UnixEpoch, [new string('b', 40)]);
        var graph = new CommitGraphControl { Commits = new[] { original } };
        var window = new Window { Content = graph }; window.Show(); await graph.LayoutReady;
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(graph)!;
            var held = peer.GetChildren()![0];
            var provider = Assert.IsAssignableFrom<ISelectionItemProvider>(held.GetProvider<ISelectionItemProvider>());
            var reloaded = original with { Subject = "Reloaded", Parents = new[] { new string('b', 40) } };
            graph.Commits = new[] { reloaded }; await graph.LayoutReady;
            provider.Select(); Assert.Same(reloaded, graph.SelectedCommit);
            Assert.Contains("Reloaded", held.GetName());
            graph.SelectedCommit = null;
            graph.Commits = Array.Empty<CommitInfo>(); await graph.LayoutReady;
            provider.Select(); Assert.Null(graph.SelectedCommit);
            Assert.True(held.IsOffscreen()); Assert.Empty(Assert.IsAssignableFrom<ISelectionProvider>(peer.GetProvider<ISelectionProvider>()).GetSelection());
        }
        finally { window.Close(); }
    }
}
