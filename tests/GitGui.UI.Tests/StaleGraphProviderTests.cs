using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GitGui.Controls;
using GitGui.Core;
using System.Runtime.CompilerServices;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class StaleGraphProviderTests
{
    [AvaloniaFact]
    public async Task Evicted_virtual_row_is_collectible_while_the_graph_remains_open()
    {
        var commits = Enumerable.Range(0, 100).Select(i => new CommitInfo(i.ToString("x40"), "Commit " + i,
            "Author", DateTimeOffset.UnixEpoch, Array.Empty<string>())).ToArray();
        var graph = new CommitGraphControl { Commits = commits };
        var window = new Window { Content = graph, Width = 640, Height = 240 };
        window.Show(); await graph.LayoutReady; window.UpdateLayout();
        try
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(graph)!;
            var evicted = EvictFirstRow(peer);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.False(evicted.IsAlive, "An offscreen row must not stay rooted by the graph's event subscriptions.");
            Assert.NotEmpty(peer.GetChildren()!);
            GC.KeepAlive(graph);
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EvictFirstRow(AutomationPeer peer)
    {
        var row = peer.GetChildren()![0];
        var reference = new WeakReference(row);
        peer.GetProvider<IScrollProvider>()!.SetScrollPercent(-1, 100);
        Assert.DoesNotContain(row, peer.GetChildren()!);
        return reference;
    }

    [AvaloniaFact]
    public async Task Virtual_row_preserves_control_state_root_and_bounds_notifications()
    {
        var commit = new CommitInfo(new string('c', 40), "Subject", "Author", DateTimeOffset.UnixEpoch, []);
        var graph = new CommitGraphControl { Commits = new[] { commit } };
        var window = new Window { Content = graph, Width = 640, Height = 240 };
        window.Show(); await graph.LayoutReady; window.UpdateLayout();
        try
        {
            var parent = ControlAutomationPeer.CreatePeerForElement(graph)!;
            var row = Assert.Single(parent.GetChildren()!);
            Assert.Same(parent, row.GetParent()); Assert.Same(parent.GetVisualRoot(), row.GetVisualRoot());
            Assert.True(row.IsControlElement()); Assert.True(row.IsContentElement());
            Assert.Equal(graph.Focusable, row.IsKeyboardFocusable()); Assert.True(row.IsEnabled());
            AutomationProperties.SetHelpText(graph, "History help"); Assert.Equal("History help", row.GetHelpText());
            var statuses = new List<string>();
            row.PropertyChanged += (_, change) =>
            {
                if (change.Property == AutomationElementIdentifiers.ItemStatusProperty && change.NewValue is string value)
                    statuses.Add(value);
            };
            AutomationProperties.SetItemStatus(graph, "History updated");
            Assert.Equal("History updated", row.GetItemStatus()); Assert.Contains("History updated", statuses);
            graph.IsEnabled = false; Assert.False(row.IsEnabled()); graph.IsEnabled = true;
            graph.Focusable = false; Assert.False(row.IsKeyboardFocusable()); graph.Focusable = true;
            var bounds = new List<Rect>();
            row.PropertyChanged += (_, change) =>
            {
                if (change.Property == AutomationElementIdentifiers.BoundingRectangleProperty && change.NewValue is Rect value)
                    bounds.Add(value);
            };
            var before = row.GetBoundingRectangle(); graph.Width = 400; window.UpdateLayout();
            Assert.NotEqual(before.Width, row.GetBoundingRectangle().Width);
            Assert.Contains(row.GetBoundingRectangle(), bounds);
            row.GetProvider<ISelectionItemProvider>()!.Select(); Assert.Same(commit, graph.SelectedCommit);
            Assert.Contains("Subject; Author;", row.GetName()); Assert.Empty(row.GetChildren());
        }
        finally { window.Close(); }
    }

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
