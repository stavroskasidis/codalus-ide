using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Rendering;
using DaedalusStudio.Debugging;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.Editor;

/// <summary>
/// PerfTips (ADR 0021): after a step or a breakpoint, the time since the program was resumed, in gray at the end of
/// the current statement's line. It is measured in the shell, adapter overhead included.
/// </summary>
public sealed class PerfTipRenderer(Func<int?> currentLine, Func<TimeSpan?> elapsed, Func<bool> enabled) : IBackgroundRenderer
{
    private static readonly IBrush s_brush = new ImmutableSolidColorBrush(Color.FromArgb(0xA0, 0x80, 0x80, 0x80));

    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The text shown, e.g. "≤ 12 ms elapsed" (null: nothing to show).</summary>
    public static string? Text(TimeSpan? time) => time is { } t
        ? $"≤ {Math.Max(1, (long)Math.Ceiling(t.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture)} ms elapsed"
        : null;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!enabled() || currentLine() is not { } number || Text(elapsed()) is not { } text || !textView.VisualLinesValid)
        {
            return;
        }

        var line = textView.VisualLines.FirstOrDefault(l => l.FirstDocumentLine.LineNumber == number);
        if (line is null)
        {
            return;
        }

        var end = line.GetVisualPosition(line.VisualLengthWithEndOfLineMarker, VisualYPosition.TextTop) - textView.ScrollOffset;
        var typeface = new Typeface(textView.GetValue(TextBlock.FontFamilyProperty), FontStyle.Italic);
        var size = textView.GetValue(TextBlock.FontSizeProperty) * 0.9;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, s_brush);
        drawingContext.DrawText(formatted, new Point(end.X + 24, end.Y + 1));
    }
}

/// <summary>
/// The exception helper (ADR 0021): opened at the current statement when the program stops on an exception, with the
/// type, the message and the inner-exception chain, "Break when this exception type is thrown", View Details
/// (QuickWatch on <c>$exception</c>) and Copy Details.
/// </summary>
internal static class ExceptionHelper
{
    public static Control Build(StopInfo stop, ExceptionSettings settings, IEditorServices services, Action close, Control owner)
    {
        var info = stop.Exception;
        var details = info?.Details;
        var type = details?.FullTypeName ?? details?.TypeName ?? info?.ExceptionId?.Replace("CLR/", string.Empty, StringComparison.Ordinal) ?? "Exception";
        var message = details?.Message ?? info?.Description ?? stop.Description ?? string.Empty;
        var unhandled = info?.BreakMode is "unhandled" or "userUnhandled";

        var panel = new StackPanel { Spacing = 6, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = unhandled ? "Exception User-Unhandled" : "Exception Thrown", FontWeight = FontWeight.Bold, FontSize = 14 });
        panel.Children.Add(new SelectableTextBlock { Text = type, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (message.Length > 0)
        {
            panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        }

        var inner = Chain(details).Skip(1).ToList();
        if (inner.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Inner exceptions", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
            for (var i = 0; i < inner.Count; i++)
            {
                var name = inner[i].FullTypeName ?? inner[i].TypeName ?? "Exception";
                panel.Children.Add(new SelectableTextBlock
                {
                    Text = $"{new string(' ', i * 2)}↳ {name}: {inner[i].Message}",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        var breakWhenThrown = new CheckBox { Content = "Break when this exception type is thrown", IsChecked = settings.Breaks(type) };
        breakWhenThrown.IsCheckedChanged += (_, _) => services.SetBreakOnException(type, breakWhenThrown.IsChecked == true);
        AutomationPropertiesName(breakWhenThrown, "Break when this exception type is thrown");
        panel.Children.Add(breakWhenThrown);

        var viewDetails = new Button { Content = "View Details" };
        viewDetails.Click += (_, _) =>
        {
            close();
            services.ShowQuickWatch(details?.EvaluateName ?? "$exception");
        };
        var copy = new Button { Content = "Copy Details" };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(owner)?.Clipboard is { } clipboard)
            {
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, Describe(type, message, details)).ConfigureAwait(true);
            }
        };
        var dismiss = new Button { Content = "Close" };
        dismiss.Click += (_, _) => close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { viewDetails, copy, dismiss } });

        var border = new Border { Classes = { "editorHover", "exceptionHelper" }, Child = panel, Padding = new Thickness(10) };
        AutomationPropertiesName(border, "Exception helper: " + type);
        return border;
    }

    /// <summary>The exception and its inner exceptions, outermost first (netcoredbg nests one level per exception).</summary>
    public static IEnumerable<ExceptionDetails> Chain(ExceptionDetails? details)
    {
        for (var current = details; current is not null; current = current.InnerException is [var next, ..] ? next : null)
        {
            yield return current;
        }
    }

    /// <summary>The text Copy Details puts on the clipboard.</summary>
    public static string Describe(string type, string message, ExceptionDetails? details)
    {
        var text = new StringBuilder().Append(type).Append(": ").AppendLine(message);
        if (details?.StackTrace is { Length: > 0 } stack)
        {
            text.AppendLine(stack);
        }

        foreach (var inner in Chain(details).Skip(1))
        {
            text.Append("Inner exception: ").Append(inner.FullTypeName ?? inner.TypeName).Append(": ").AppendLine(inner.Message);
            if (inner.StackTrace is { Length: > 0 } innerStack)
            {
                text.AppendLine(innerStack);
            }
        }

        return text.ToString();
    }

    private static void AutomationPropertiesName(Control control, string name) => Avalonia.Automation.AutomationProperties.SetName(control, name);
}
