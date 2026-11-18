using Avalonia.Controls;
using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using AvaloniaEdit.Document;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class IncrementalTextViewTests
{
    [AvaloniaFact]
    public void Decorative_drawing_layers_are_not_document_automation_children()
    {
        var frames = new Queue<Action>();
        var text = string.Join('\n', Enumerable.Range(0, 1000).Select(i => "中文 🙂 line " + i));
        var view = new IncrementalTextView(frames.Enqueue) { Document = new TextDocument(text) };
        var window = new Window { Content = view, Width = 440, Height = 420 };
        view.BeginUpdate(); window.Show();
        try
        {
            window.UpdateLayout();
            var documentPeer = ControlAutomationPeer.CreatePeerForElement(view)!;
            Assert.False(documentPeer.IsControlElement()); Assert.False(documentPeer.IsContentElement());
            Assert.Empty(documentPeer.GetChildren());
            Assert.Equal(text, view.Document.Text);
            var action = new Button { Content = "Inspect document" };
            view.Layers.Add(action); window.UpdateLayout();
            var peer = ControlAutomationPeer.CreatePeerForElement(view)!;
            Assert.Contains(peer.GetChildren(), child => child.GetName() == "Inspect document");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Closing_during_layout_cancels_readiness_and_queued_frame_cannot_resume_it()
    {
        var frames = new Queue<Action>();
        var view = new IncrementalTextView(frames.Enqueue) { Document = new TextDocument(new string('\n', 1000)) };
        var window = new Window { Content = view, Width = 440, Height = 420 };
        view.BeginUpdate(); window.Show(); window.UpdateLayout();
        var ready = view.DisplayReady; Assert.False(ready.IsCompleted);
        window.Close(); Assert.True(ready.IsCanceled);
        while (frames.TryDequeue(out var action)) action();
        Assert.True(ready.IsCanceled);
    }
    [AvaloniaFact]
    public void Full_visible_document_is_ready_only_after_frame_slices_finish()
    {
        var frames = new Queue<Action>();
        var view = new IncrementalTextView(frames.Enqueue)
        {
            Document = new TextDocument(string.Join('\n', Enumerable.Range(0, 1000).Select(i => "中文 🙂 line " + i)))
        };
        var window = new Window { Content = view, Width = 440, Height = 420 };
        view.BeginUpdate(); window.Show();
        try
        {
            window.UpdateLayout(); var ready = view.DisplayReady;
            Assert.False(ready.IsCompleted);
            Assert.InRange(view.VisualLines.Count, 1, 4);
            var previous = view.VisualLines.Count; var turns = 0;
            while (!ready.IsCompleted && turns++ < 20)
            {
                Assert.Single(frames); frames.Dequeue()(); window.UpdateLayout();
                Assert.InRange(view.VisualLines.Count - previous, 0, 4);
                previous = view.VisualLines.Count;
            }
            Assert.True(ready.IsCompletedSuccessfully);
            Assert.True(view.VisualLines.Count > 8);
            Assert.True(view.VisualLines.Sum(line => line.Height) >= view.Bounds.Height);
            Assert.Equal(view.Document.Text, string.Join('\n', Enumerable.Range(0, 1000).Select(i => "中文 🙂 line " + i)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Replaced_document_cancels_old_readiness_and_stale_frame_cannot_finish_new_document()
    {
        var frames = new Queue<Action>();
        var view = new IncrementalTextView(frames.Enqueue) { Document = new TextDocument(new string('\n', 1000)) };
        var window = new Window { Content = view, Width = 440, Height = 420 };
        view.BeginUpdate(); window.Show();
        try
        {
            window.UpdateLayout(); var old = view.DisplayReady; var stale = frames.Dequeue();
            view.BeginUpdate(); view.Document.Text = "replacement"; window.UpdateLayout();
            Assert.True(old.IsCanceled);
            var current = view.DisplayReady; stale(); window.UpdateLayout();
            Assert.True(current.IsCompletedSuccessfully);
            Assert.Equal("replacement", view.Document.Text);
            Assert.Single(view.VisualLines);
        }
        finally { window.Close(); }
    }
}
