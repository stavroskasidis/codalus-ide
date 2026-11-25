using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using LspCompletionItem = Codalus.LanguageServices.Protocol.CompletionItem;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// What a completion commit replaces, against the text right of the caret. The list's ranges are positions in the text
/// as it was when the list was asked for: what was typed since went in at the caret, so a range end after the request's
/// position lies that much further on. Roslyn's span covers the rest of the word when the list is asked for in the
/// middle of one: it filters the list too, as the classic IDE does, so typing right before a word never replaces it.
/// (A review of 1.0 on macOS: "text getting deleted as if some kind of overwrite mode were on".)
/// </summary>
public sealed class CompletionRangeTests
{
    private const string Method = "class C\n{\n    void M()\n    {\n        InitializeComponent();\n    }\n}\n";

    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string name, string text)
    {
        var path = Path.Combine(f.UserData, name);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        var editor = f.Shell.ActiveEditor!.Controller!;
        editor.Editor.TextArea.Focus();
        return editor;
    }

    /// <summary>An item as the C# host sends it once resolved: its edit over <paramref name="range"/>.</summary>
    private static CompletionData Item(CodeEditorController editor, string label, Range range, string? newText = null, string commitCharacters = " ;(")
    {
        var resolved = new LspCompletionItem(label) { TextEdit = new TextEdit(range, newText ?? label) };
        var data = editor.NewCompletionData(new LspCompletionItem(label) { Data = JsonElement.Parse("1") }, _ => Task.FromResult(resolved), commitCharacters);
        _ = data.Resolve(); // prefetched, as when the item is selected
        return data;
    }

    [AvaloniaFact]
    public async Task Typing_right_before_a_word_keeps_it_when_the_list_covers_its_rest()
    {
        // "v" typed before "InitializeComponent": Roslyn's span is "vInitializeComponent". The list filtered by "v" alone
        // hard-selected "var", and Space replaced the span, measured in the text before "ar" was typed: "var nt();".
        await using var f = ShellFixture.Create();
        var editor = await OpenAsync(f, "before.txt", Method);
        var at = Method.IndexOf("InitializeComponent", StringComparison.Ordinal);
        editor.Editor.CaretOffset = at;
        editor.Editor.TextArea.PerformTextInput("v");
        var span = new Range(new Position(4, 8), new Position(4, 28));
        editor.OpenCompletion(at, [Item(editor, "var", span), Item(editor, "void", span)], span, requestedAt: new Position(4, 9));

        // Nothing matches the word with its rest, so the list shows nothing and Space types.
        Assert.Empty(editor.CompletionWindow!.CompletionList.CurrentList);
        editor.Editor.TextArea.PerformTextInput("ar");
        editor.Editor.TextArea.PerformTextInput(" ");
        Assert.Contains("        var InitializeComponent();\n", editor.Model.Text.Text, StringComparison.Ordinal);
        Assert.False(editor.IsCompletionOpen);
    }

    [AvaloniaFact]
    public async Task Completing_in_the_middle_of_a_word_replaces_the_whole_word()
    {
        // Ctrl+Space in "Initi|alizeComponent": the list is filtered by the whole word, and Enter replaces all of it.
        await using var f = ShellFixture.Create();
        var editor = await OpenAsync(f, "middle.txt", Method);
        var at = Method.IndexOf("InitializeComponent", StringComparison.Ordinal);
        editor.Editor.CaretOffset = at + "Initi".Length;
        var span = new Range(new Position(4, 8), new Position(4, 27));
        editor.OpenCompletion(at, [Item(editor, "InitializeComponent", span), Item(editor, "Initialize", span)], span, requestedAt: new Position(4, 13));
        Assert.Equal("InitializeComponent", editor.CompletionWindow!.CompletionList.SelectedItem?.Label);

        f.Press(Key.Enter);
        Assert.Equal(Method, editor.Model.Text.Text);
        Assert.Equal(at + "InitializeComponent".Length, editor.Editor.CaretOffset);
        Assert.False(editor.IsCompletionOpen);
    }

    [AvaloniaFact]
    public async Task An_edit_past_the_request_ends_where_the_typing_moved_it()
    {
        // A server's item replaces the quotes it was asked in ("|" to "alpha"). Its end is a position in the text before
        // "al" was typed: read in the text after it, it fell inside the typed word and left the closing quote twice.
        await using var f = ShellFixture.Create();
        const string Text = "key: \"\"\n";
        var editor = await OpenAsync(f, "quoted.txt", Text);
        editor.Editor.CaretOffset = 6;
        var quotes = new Range(new Position(0, 5), new Position(0, 7));
        var item = Item(editor, "alpha", quotes, newText: "\"alpha\"");
        editor.Editor.TextArea.PerformTextInput("al");
        editor.OpenCompletion(6, [item], range: null, requestedAt: new Position(0, 6));
        Assert.Equal("alpha", editor.CompletionWindow!.CompletionList.SelectedItem?.Label);

        f.Press(Key.Enter);
        Assert.Equal("key: \"alpha\"\n", editor.Model.Text.Text);
    }
}
