using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Editor.IntelliSense;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// The IntelliSense popups in the shipped app (ADR 0081), on the BuildAndRun solution's Program.cs. Typing a call opens
/// signature help: the call site's types in the editor's colors, the active parameter bold, the overloads' chevrons.
/// Completion then opens below the line with signature help above it, its kind icons, filter bar and the selected item's
/// description beside it (the website's picture, <c>smoke-test-intellisense.png</c>). Quick Info on a variable with a
/// warning shows the symbol, the diagnostic's link and Fix, and doesn't open while the popups are (<c>smoke-test-quick-info.png</c>).
/// </summary>
internal sealed partial class SmokeTest
{
    private async Task IntelliSenseStepAsync()
    {
        var program = await OpenAsync(Program).ConfigureAwait(true);
        var document = program.Model.Text;
        var original = document.Text;
        try
        {
            // A call on a line of its own, in the middle of the file so signature help has room above it.
            var line = document.GetLineByNumber(3);
            document.Insert(line.Offset, "int unused = 0;\nConsole.WriteLine(\"{0}\", \n");
            MoveCaret(program, "Console.WriteLine(\"{0}\", ", "Console.WriteLine(\"{0}\", ".Length);
            await RenderedAsync().ConfigureAwait(true);

            program.ShowSignatureHelp();
            await WaitAsync(() => program.SignatureWindow is { IsOpen: true, Count: > 1 } w && w.Current.Parts is { Length: > 0 }, "signature help lists Console.WriteLine's overloads with classified parts", 90).ConfigureAwait(true);
            var signature = program.SignatureWindow!;
            var runs = signature.SignatureBlock.Inlines!.OfType<Run>().ToList();
            Check(runs.Any(r => r.Text == "WriteLine" && r.Foreground is ISolidColorBrush), "the method's name is in the editor's method color");
            Check(runs.Any(r => r.FontWeight == FontWeight.Bold), "the active parameter is bold");
            Check(signature.PositionText.Contains(" of ", StringComparison.Ordinal), $"the chevrons say which overload ({signature.PositionText})");

            // Completion below the line, signature help above it.
            program.Editor.TextArea.PerformTextInput("Con");
            program.ShowCompletion();
            await WaitAsync(() => program.CompletionWindow is { IsOpen: true } w && w.CompletionList.SelectedItem?.Label == "Console", "completion selects Console", 90).ConfigureAwait(true);
            var completion = program.CompletionWindow!;
            await WaitAsync(() => completion.DescriptionTip.IsOpen, "the selected item's description shows beside it", 30).ConfigureAwait(true);
            Check(completion.Side == PopupSide.Below && program.SignatureWindow?.Side == PopupSide.Above, $"the list is below the line and signature help above it ({completion.Side}, {program.SignatureWindow?.Side})");
            Check(completion.CompletionList.SelectedItem!.Icon == "symbol-class", "Console has the class icon");
            Check(completion.CompletionList.CurrentList.Select(d => d.Icon).Distinct().Count() > 2, "the items have their kinds' icons");
            Check(completion.CompletionList.FilterToggles.ContainsKey("classes"), "the filter bar has Classes");
            Check(completion.DescriptionContent is DescriptionView { SignatureBlock: { } description } && DescriptionView.TextOf(description).Contains("System.Console", StringComparison.Ordinal), "the description names the class");
            await Task.Delay(300).ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            CheckPopupTextSize("signature help", signature, signature.SignatureBlock, signature.SummaryBlock);
            CheckPopupTextSize("the completion list", completion, completion.CompletionList.ListBox.GetVisualDescendants().OfType<Controls.HighlightedText>().FirstOrDefault());
            CheckPopupTextSize("the description", completion.DescriptionTip, (completion.DescriptionContent as DescriptionView)?.SignatureBlock);
            Screenshot("smoke-test-intellisense.png", new Popup?[] { signature, completion, completion.DescriptionTip }.OfType<Popup>());

            // Quick Info stays closed while they're open; Escape closes the list, then signature help.
            program.ShowQuickInfoAtCaret();
            await Task.Delay(500).ConfigureAwait(true);
            Check(!program.IsQuickInfoOpen, "Quick Info doesn't open over the popups");
            program.CloseIntelliSense();
            await WaitAsync(() => !program.IsIntelliSenseOpen, "the popups close").ConfigureAwait(true);

            // Quick Info on a variable with a warning: the symbol, the diagnostic's link, Fix and Other Fixes.
            await WaitAsync(() => program.Markers.Segments.Any(s => s.Diagnostic.Code == "CS0219"), "the unused variable has its warning", 90).ConfigureAwait(true);
            MoveCaret(program, "int unused", "int unu".Length);
            program.ShowQuickInfoAtCaret();
            await WaitAsync(() => program.IsQuickInfoOpen && program.LastQuickInfo is { Symbol: not null, DiagnosticLinks.Count: > 0, FixButton: not null }, "Quick Info shows the variable, its warning and Fix", 60).ConfigureAwait(true);
            Check(program.LastQuickInfo!.DiagnosticLinks[0].Content as string == "CS0219", "the warning's code is a link");
            await RenderedAsync().ConfigureAwait(true);
            CheckPopupTextSize("Quick Info", program.HoverPopup, program.LastQuickInfo.Symbol?.SignatureBlock,
                program.LastQuickInfo.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("quickInfoDiagnostic")));
            Screenshot("smoke-test-quick-info.png", [program.HoverPopup]);
            program.HoverPopup.IsOpen = false;
        }
        finally
        {
            program.CloseIntelliSense();
            while (document.Text != original && document.UndoStack.CanUndo)
            {
                document.UndoStack.Undo();
            }
        }
    }

    /// <summary>
    /// A popup's text is the window's UI size (Shell.FontSize.Body). In the windowed app the popup is a window of its own,
    /// whose content inherits from its root (Fluent's 14 px unless the shell's styles set it), not from the main window.
    /// </summary>
    private void CheckPopupTextSize(string what, Popup popup, params TextBlock?[] blocks)
    {
        Check(popup.IsOpen, $"{what} is open");
        foreach (var block in blocks)
        {
            Check(block is not null, $"{what} has its text");
            var root = TopLevel.GetTopLevel(block)?.GetType().Name ?? "no window";
            Check(Math.Abs(block!.FontSize - window.FontSize) < 0.01, $"{what}'s text is {block.FontSize:0.#} px in its {root}, the window's {window.FontSize:0.#}");
        }
    }
}
