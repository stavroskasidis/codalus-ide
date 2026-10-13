using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Workbench.Languages;

namespace Codalus.Shell;

/// <summary>
/// Scoped CSS (M16d, ADR 0027): in a component's <c>.razor.css</c> (or a page's <c>.cshtml.css</c>), selectors complete
/// the class names and ids of the component's markup, and Blazor's <c>::deep</c>. The file nests under its component
/// in Solution Explorer by the usual rule (an added extension).
/// </summary>
public sealed partial class ShellHost
{
    IReadOnlyList<CompletionItem> IEditorServices.ExtraCompletions(CodeDocument document, int offset)
    {
        if (!ScopedCss.IsScoped(document.Path))
        {
            return [];
        }

        var (kind, wordStart) = ScopedCss.At(document.Text.Text, offset);
        if (kind == ScopedCssCompletion.None)
        {
            return [];
        }

        if (kind == ScopedCssCompletion.PseudoElement)
        {
            return
            [
                new CompletionItem("::deep")
                {
                    Kind = CompletionItemKind.Keyword,
                    TextEdit = new TextEdit(new LanguageServices.Protocol.Range(document.PositionAt(wordStart - 2), document.PositionAt(offset)), "::deep"),
                    SortText = "::deep",
                    Detail = "Blazor scoped CSS",
                    Documentation = MarkupContent.Markdown("Applies the rule to the elements of child components too: `.parent ::deep h2` matches `h2` elements that child components render inside `.parent`."),
                },
            ];
        }

        var component = ScopedCss.Component(document.Path);
        string markup;
        if (Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, component)) is { } open)
        {
            markup = open.Model.Text.Text;
        }
        else
        {
            try
            {
                markup = File.ReadAllText(component);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        var (classes, ids) = ScopedCss.Names(markup);
        var what = kind == ScopedCssCompletion.ClassName ? "class" : "id";
        return [.. (kind == ScopedCssCompletion.ClassName ? classes : ids).Select(name => new CompletionItem(name)
        {
            Kind = CompletionItemKind.Value,
            InsertText = name,
            FilterText = name,
            SortText = "0" + name,
            Detail = $"{what} in {Path.GetFileName(component)}",
        })];
    }
}
