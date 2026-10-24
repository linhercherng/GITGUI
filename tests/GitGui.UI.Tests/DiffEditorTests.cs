using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using GitGui.UI;
using Xunit;

namespace GitGui.UI.Tests;

public sealed class DiffEditorTests
{
    [AvaloniaFact]
    public void Inline_diff_does_not_create_unused_split_editors()
    {
        var viewer = new DiffEditor { Source = " context\n+new", LeftSource = "old", RightSource = "new" };
        var window = new Window { Content = viewer }; window.Show();
        try
        {
            window.UpdateLayout();
            var editor = Assert.Single(viewer.GetVisualDescendants().OfType<CodeEditor>());
            Assert.Equal(viewer.Source, editor.CodeText);
            viewer.DocumentName = "Changes";
            Assert.Equal("Changes", editor.AccessibleName);
            Assert.True(editor.IsReadOnly);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void First_split_uses_latest_sources_and_retains_documents_across_mode_switches()
    {
        var viewer = new DiffEditor { Source = " context", LeftSource = "before", RightSource = "after",
            DocumentName = "Changes", BeforeLabel = "Before", AfterLabel = "After" };
        var window = new Window { Content = viewer }; window.Show();
        try
        {
            window.UpdateLayout();
            var inline = Assert.Single(viewer.GetVisualDescendants().OfType<CodeEditor>(), e => e.IsEffectivelyVisible);
            var inlineDocument = inline.Document;
            viewer.LeftSource = "latest before"; viewer.RightSource = "latest after";
            viewer.IsSplit = true; window.UpdateLayout();
            var pair = viewer.GetVisualDescendants().OfType<CodeEditor>().Where(e => e.IsEffectivelyVisible).ToArray();
            Assert.Equal(2, pair.Length);
            Assert.Equal(new[] { "latest before", "latest after" }, pair.Select(e => e.CodeText));
            Assert.Equal(new[] { "Changes · Before", "Changes · After" }, pair.Select(e => e.AccessibleName));
            var documents = pair.Select(e => e.Document).ToArray();
            viewer.IsSplit = false; window.UpdateLayout();
            Assert.Same(inline, Assert.Single(viewer.GetVisualDescendants().OfType<CodeEditor>(), e => e.IsEffectivelyVisible));
            Assert.Same(inlineDocument, inline.Document);
            viewer.DocumentName = "Updated"; viewer.BeforeLabel = "原始"; viewer.AfterLabel = "修改";
            viewer.IsSplit = true; window.UpdateLayout();
            Assert.Equal(documents, pair.Select(e => e.Document));
            Assert.Equal(new[] { "Updated · 原始", "Updated · 修改" }, pair.Select(e => e.AccessibleName));
            Assert.All(pair, editor => Assert.True(editor.IsReadOnly));
        }
        finally { window.Close(); }
    }
}
