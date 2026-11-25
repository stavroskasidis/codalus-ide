using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>
/// A read-only viewer shown the same text again (a peek, a result view's refresh) keeps its grammar's tokens (M66). Each
/// <c>TokenizerFor</c> is a new tokenizer since the Tint migration, so the viewer keys what it tokenized by the grammar's
/// scope, not by the tokenizer.
/// </summary>
public sealed class CodeViewerTokenizeTests
{
    [AvaloniaFact]
    public async Task The_same_text_shown_again_is_tokenized_once_and_a_new_text_again()
    {
        await using var f = ShellFixture.Create();
        var viewer = new CodeViewer(f.Shell, f.Shell.EditorFont) { EditorColors = true };

        viewer.Show("class C { int x = 1; }", "csharp", null);
        Assert.Equal(1, viewer.Tokenizations);

        viewer.Show("class C { int x = 1; }", "csharp", null);
        Assert.Equal(1, viewer.Tokenizations);

        viewer.Show("class D { }", "csharp", null);
        Assert.Equal(2, viewer.Tokenizations);
    }
}
