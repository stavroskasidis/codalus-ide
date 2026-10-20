using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>
/// Classified text (the C# service's <c>codalus/</c> runs) as text runs in the editor's colors: each run takes the color
/// the editor's theme gives its token type, so Quick Info, signature help and the completion tooltip match the code.
/// Plain text and kinds the theme leaves alone take the popup's text color.
/// </summary>
public static partial class ClassifiedInlines
{
    private static readonly Dictionary<uint, IBrush> s_brushes = [];

    /// <summary>The brush of a run's kind in <paramref name="theme"/>, or null for the surrounding text's color.</summary>
    public static IBrush? BrushOf(string kind, EditorTheme theme)
    {
        if (kind is ClassifiedRun.PlainText or ClassifiedRun.LineBreak || theme.TokenColor(kind) is not { } argb)
        {
            return null;
        }

        lock (s_brushes)
        {
            if (!s_brushes.TryGetValue(argb, out var brush))
            {
                brush = new ImmutableSolidColorBrush(argb);
                s_brushes[argb] = brush;
            }

            return brush;
        }
    }

    /// <summary>
    /// Adds the runs to <paramref name="inlines"/>; the runs of parameter <paramref name="boldParameter"/> are bold (the
    /// active parameter of signature help).
    /// </summary>
    public static void Add(InlineCollection inlines, IEnumerable<ClassifiedRun> runs, EditorTheme theme, int? boldParameter = null)
    {
        foreach (var run in runs)
        {
            if (run.Kind == ClassifiedRun.LineBreak)
            {
                inlines.Add(new LineBreak());
                continue;
            }

            var inline = new Run(run.Text);
            if (BrushOf(run.Kind, theme) is { } brush)
            {
                inline.Foreground = brush;
            }

            if (boldParameter is { } bold && run.Parameter == bold)
            {
                inline.FontWeight = FontWeight.Bold;
            }

            inlines.Add(inline);
        }
    }

    /// <summary>A wrapping text block of the runs.</summary>
    public static TextBlock Block(IEnumerable<ClassifiedRun> runs, EditorTheme theme, int? boldParameter = null)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Add(block.Inlines!, runs, theme, boldParameter);
        return block;
    }

    /// <summary>
    /// A signature from a server without classified parts (other languages' plain LSP signature help): the label as
    /// plain text, each parameter's substring marked with its index so the active one can still be bold.
    /// </summary>
    public static ClassifiedRun[] FromLabel(SignatureInformation signature)
    {
        var runs = new List<ClassifiedRun>();
        var label = signature.Label;
        var position = 0;
        for (var i = 0; i < signature.Parameters.Length; i++)
        {
            var parameter = signature.Parameters[i].Label;
            var index = parameter.Length == 0 ? -1 : label.IndexOf(parameter, position, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            if (index > position)
            {
                runs.Add(new ClassifiedRun(label[position..index], ClassifiedRun.PlainText));
            }

            runs.Add(new ClassifiedRun(parameter, ClassifiedRun.PlainText) { Parameter = i });
            position = index + parameter.Length;
        }

        if (position < label.Length)
        {
            runs.Add(new ClassifiedRun(label[position..], ClassifiedRun.PlainText));
        }

        return [.. runs];
    }

    /// <summary>A signature's runs: the server's classified parts, or its label's.</summary>
    public static ClassifiedRun[] Of(SignatureInformation signature) => signature.Parts is { Length: > 0 } parts ? parts : FromLabel(signature);

    /// <summary>
    /// A parameter's name in a signature: its <c>parameter</c> run, or the label's last word before a default value
    /// (<c>int count = 0</c> → <c>count</c>).
    /// </summary>
    public static string ParameterName(SignatureInformation signature, int index)
    {
        if (signature.Parts?.FirstOrDefault(r => r.Parameter == index && r.Kind == "parameter") is { } run)
        {
            return run.Text;
        }

        // "int count = 0" (C#), "count: number" (TypeScript, Python, F#), "count As Integer" (Visual Basic).
        var label = signature.Parameters[index].Label;
        var equals = label.IndexOf('=', StringComparison.Ordinal);
        var declaration = (equals >= 0 ? label[..equals] : label).Trim();
        var colon = declaration.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            return declaration[..colon].Trim().TrimEnd('?');
        }

        var visualBasic = declaration.IndexOf(" As ", StringComparison.Ordinal);
        if (visualBasic > 0)
        {
            declaration = declaration[..visualBasic].Trim();
            return declaration[(declaration.LastIndexOf(' ') + 1)..];
        }

        var space = declaration.LastIndexOfAny([' ', '\t']);
        return (space >= 0 ? declaration[(space + 1)..] : declaration).TrimEnd('?');
    }

    /// <summary>Documentation Markdown as plain text: no code fences or escapes (the server escapes plain documentation).</summary>
    public static string PlainText(string markdown) =>
        Escape().Replace(string.Join('\n', markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => !l.StartsWith("```", StringComparison.Ordinal))), "$1").Trim();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\([!-/:-@\[-`{-~])")]
    private static partial System.Text.RegularExpressions.Regex Escape();
}
