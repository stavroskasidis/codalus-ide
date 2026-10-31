using System.Text;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Output;

/// <summary>What a line of output reports, for its color in the Output window (M34h).</summary>
public enum OutputLineKind
{
    None,
    Error,
    Warning,
    Success,
}

/// <summary>
/// Classifies a line of output by what it says (M34h): MSBuild's and the compilers' diagnostics (<c>error CS1234:</c>,
/// <c>warning NU1603:</c>), a build's or a test run's outcome, an exception and its stack trace, and the log levels of
/// .NET's console logger (<c>fail:</c>, <c>warn:</c>). It reads one line, without allocating, so a view can classify the
/// lines it shows as it draws them.
/// </summary>
public static partial class OutputClassifier
{
    /// <summary>Lines longer than this are classified by their start (a diagnostic or an outcome says what it is first).</summary>
    public const int MaxClassifiedLength = 1024;

    public static OutputLineKind Classify(ReadOnlySpan<char> line)
    {
        if (line.Length > MaxClassifiedLength)
        {
            line = line[..MaxClassifiedLength];
        }

        line = line.TrimEnd();
        if (line.IsEmpty)
        {
            return OutputLineKind.None;
        }

        // An outcome first: "Build succeeded: 0 errors, 2 warnings." is a success that mentions warnings.
        if (Success().IsMatch(line))
        {
            return OutputLineKind.Success;
        }

        if (Error().IsMatch(line))
        {
            return OutputLineKind.Error;
        }

        return Warning().IsMatch(line) ? OutputLineKind.Warning : OutputLineKind.None;
    }

    // "error CS1234:", "error MSB4019:", "error NU1101:", "error NETSDK1045:", "file(1,2): error: …", "EXEC : error : …",
    // "error: …", "fatal: …", the console logger's "fail:" and "crit:", "Build FAILED.", "Restore failed: …", "Unhandled
    // exception.", "System.InvalidOperationException: …", a stack frame ("   at Lib.Calc.Run() in …"), "--- End of stack
    // trace …", "1 Error(s)", "2 succeeded, 1 failed", the test runners' "Failed!", "Failed Lib.Tests.Add [12 ms]", "[FAIL]" and "Test Run Failed.".
    [GeneratedRegex(
        @"\berror\s+[A-Z]{1,10}\d{1,6}\s*:|:\s*error\s*:|^\s*(?i:error|fatal|fail|crit)\s*:|^\s*[A-Za-z]+(\s+[A-Za-z]+)?\s+(failed|FAILED)\b|^\s*Unhandled exception\b|^\s*([A-Za-z_]\w*\.)+[A-Za-z_]\w*Exception\b\s*(:|$)|^\s+at\s+[^\s(]+\(.*\)|^\s*---\s*End of (inner exception )?stack trace|^\s*--->\s|\b[1-9]\d*\s+Error\(s\)|\b[1-9]\d*\s+failed\b|^\s*Failed!\s|^\s*Failed\s+[A-Za-z_][\w.`<>,\[\]]*(\s|$)|\[FAIL\]|^\s*Test Run (Failed|Aborted)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Error();

    // "warning CS0168:", "warning NU1603:", "file(1,2): warning: …", "warning: …", the console logger's "warn:", "2 Warning(s)".
    [GeneratedRegex(
        @"\bwarning\s+[A-Z]{1,10}\d{1,6}\s*:|:\s*warning\s*:|^\s*(?i:warning|warn)\s*:|\b[1-9]\d*\s+Warning\(s\)|\[SKIP\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex Warning();

    // "Build succeeded.", "Rebuild succeeded: 0 errors, 1 warning.", "0 Error(s)", "Passed!", "Test Run Successful.",
    // "========== Build: 3 succeeded, 0 failed …".
    [GeneratedRegex(
        @"^\s*[A-Za-z]+(\s+[A-Za-z]+)?\s+succeeded\b|^\s*0\s+Error\(s\)|^\s*Passed!\s|^\s*Test Run Successful|\b\d+\s+succeeded,\s+0\s+failed\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex Success();
}

/// <summary>Text attributes from ANSI escape sequences (SGR) and standard error (M34h).</summary>
[Flags]
public enum OutputAttributes : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Dim = 8,

    /// <summary>The text came from standard error.</summary>
    Error = 16,
}

/// <summary>
/// A run's style: <see cref="Foreground"/> is -1 for the text's own color, 0–255 for the ANSI palette (16 colors, the
/// 6×6×6 cube, the grays), or <see cref="RgbFlag"/> with a 24-bit color.
/// </summary>
public readonly record struct OutputStyle(int Foreground, OutputAttributes Attributes)
{
    public const int RgbFlag = 0x1000000;

    public static OutputStyle Default { get; } = new(-1, OutputAttributes.None);

    public bool IsDefault => Foreground < 0 && Attributes == OutputAttributes.None;

    public bool IsRgb => Foreground >= RgbFlag;
}

/// <summary>A styled range of decoded output.</summary>
public readonly record struct OutputRun(int Start, int Length, OutputStyle Style)
{
    public int End => Start + Length;
}

/// <summary>
/// Decodes a stream of output with ANSI escape sequences (M34h): SGR's colors and attributes become runs, every other
/// sequence (cursor moves, erasing, OSC titles and links, the terminal logger's progress) is dropped, so nothing shows
/// raw. It keeps its state between chunks, so a color set in one append colors the next, and a sequence split across
/// two appends is still recognized.
/// </summary>
public sealed class AnsiDecoder
{
    private const char Escape = '\u001b';
    private const int MaxPending = 256;

    private readonly StringBuilder _pending = new();
    private OutputStyle _style = OutputStyle.Default;

    /// <summary>The characters decoded since the last <see cref="Reset"/>: the next run's offset is counted from there.</summary>
    public int Length { get; private set; }

    /// <summary>Forgets the colors, an unfinished sequence and the length (a channel re-read from its start).</summary>
    public void Reset()
    {
        _pending.Clear();
        _style = OutputStyle.Default;
        Length = 0;
    }

    /// <summary>
    /// Counts the next run's offset from 0 again, keeping the colors and an unfinished sequence: each chunk's runs from
    /// the chunk's start (a view that drops its own oldest text never reads the channel again, so the length only grew).
    /// </summary>
    public void Rebase() => Length = 0;

    /// <summary>
    /// Decodes <paramref name="text"/>: its visible characters are appended to <paramref name="output"/>, and runs for its
    /// styled parts to <paramref name="runs"/> (offsets counted as <see cref="Length"/>). <paramref name="isError"/> marks
    /// the whole text as standard error's.
    /// </summary>
    public void Decode(ReadOnlySpan<char> text, bool isError, StringBuilder output, List<OutputRun> runs)
    {
        if (_pending.Length > 0)
        {
            // The last chunk ended inside a sequence: decode it with this one (rare, so the copy doesn't matter).
            var joined = string.Concat(_pending.ToString().AsSpan(), text);
            _pending.Clear();
            DecodeCore(joined, isError, output, runs);
            return;
        }

        DecodeCore(text, isError, output, runs);
    }

    private void DecodeCore(ReadOnlySpan<char> text, bool isError, StringBuilder output, List<OutputRun> runs)
    {
        while (!text.IsEmpty)
        {
            var escape = text.IndexOf(Escape);
            var plain = escape < 0 ? text : text[..escape];
            if (!plain.IsEmpty)
            {
                Emit(plain, isError, output, runs);
            }

            if (escape < 0)
            {
                return;
            }

            text = text[escape..];
            var length = SequenceLength(text, out var complete);
            if (!complete)
            {
                AppendPending(text);
                return;
            }

            Apply(text[..length]);
            text = text[length..];
        }
    }

    private void AppendPending(ReadOnlySpan<char> text)
    {
        _pending.Append(text);
        if (_pending.Length > MaxPending)
        {
            // Not a sequence after all: drop it rather than hold the output back.
            _pending.Clear();
        }
    }

    private void Emit(ReadOnlySpan<char> plain, bool isError, StringBuilder output, List<OutputRun> runs)
    {
        var style = isError ? _style with { Attributes = _style.Attributes | OutputAttributes.Error } : _style;
        output.Append(plain);
        if (!style.IsDefault)
        {
            if (runs.Count > 0 && runs[^1].End == Length && runs[^1].Style == style)
            {
                runs[^1] = runs[^1] with { Length = runs[^1].Length + plain.Length };
            }
            else
            {
                runs.Add(new OutputRun(Length, plain.Length, style));
            }
        }

        Length += plain.Length;
    }

    /// <summary>The length of the escape sequence at the start of <paramref name="text"/> (ESC first).</summary>
    private static int SequenceLength(ReadOnlySpan<char> text, out bool complete)
    {
        complete = true;
        if (text.Length < 2)
        {
            complete = false;
            return text.Length;
        }

        switch (text[1])
        {
            case '[':
                // CSI: parameters and intermediates, then a final byte in @–~.
                for (var i = 2; i < text.Length; i++)
                {
                    if (text[i] is >= '@' and <= '~')
                    {
                        return i + 1;
                    }
                }

                complete = false;
                return text.Length;
            case ']' or 'P' or '_' or '^' or 'X':
                // OSC, DCS and the other strings: until BEL or ST (ESC \).
                for (var i = 2; i < text.Length; i++)
                {
                    if (text[i] == '\u0007')
                    {
                        return i + 1;
                    }

                    if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\')
                    {
                        return i + 2;
                    }
                }

                complete = false;
                return text.Length;
            case '(' or ')' or '*' or '+' or '#' or '%':
                // A character set or line size: one more character.
                if (text.Length < 3)
                {
                    complete = false;
                    return text.Length;
                }

                return 3;
            default:
                // ESC and one character (ESC 7, ESC =, ESC M…).
                return 2;
        }
    }

    /// <summary>Applies a whole sequence: SGR changes the style, anything else is dropped.</summary>
    private void Apply(ReadOnlySpan<char> sequence)
    {
        if (sequence.Length < 3 || sequence[1] != '[' || sequence[^1] != 'm')
        {
            return;
        }

        var parameters = sequence[2..^1];
        if (parameters.IsEmpty)
        {
            _style = OutputStyle.Default;
            return;
        }

        Span<int> values = stackalloc int[16];
        var count = 0;
        var value = 0;
        var has = false;
        foreach (var c in parameters)
        {
            if (c is >= '0' and <= '9')
            {
                value = Math.Min((value * 10) + (c - '0'), 100_000);
                has = true;
            }
            else if (c is ';' or ':')
            {
                if (count < values.Length)
                {
                    values[count++] = has ? value : 0;
                }

                value = 0;
                has = false;
            }
            else
            {
                return; // private parameters ("?", "<", ">"): not SGR
            }
        }

        if (count < values.Length)
        {
            values[count++] = has ? value : 0;
        }

        var foreground = _style.Foreground;
        var flags = _style.Attributes;
        for (var i = 0; i < count; i++)
        {
            var p = values[i];
            switch (p)
            {
                case 0:
                    foreground = -1;
                    flags = OutputAttributes.None;
                    break;
                case 1:
                    flags |= OutputAttributes.Bold;
                    break;
                case 2:
                    flags |= OutputAttributes.Dim;
                    break;
                case 3:
                    flags |= OutputAttributes.Italic;
                    break;
                case 4:
                    flags |= OutputAttributes.Underline;
                    break;
                case 22:
                    flags &= ~(OutputAttributes.Bold | OutputAttributes.Dim);
                    break;
                case 23:
                    flags &= ~OutputAttributes.Italic;
                    break;
                case 24:
                    flags &= ~OutputAttributes.Underline;
                    break;
                case >= 30 and <= 37:
                    foreground = p - 30;
                    break;
                case >= 90 and <= 97:
                    foreground = p - 90 + 8;
                    break;
                case 39:
                    foreground = -1;
                    break;
                case 38 or 48 or 58:
                    // An extended color: 5;n (the palette) or 2;r;g;b. Backgrounds and underline colors are skipped.
                    if (i + 2 < count && values[i + 1] == 5)
                    {
                        if (p == 38)
                        {
                            foreground = Math.Clamp(values[i + 2], 0, 255);
                        }

                        i += 2;
                    }
                    else if (i + 4 < count && values[i + 1] == 2)
                    {
                        if (p == 38)
                        {
                            foreground = OutputStyle.RgbFlag | (Math.Clamp(values[i + 2], 0, 255) << 16) | (Math.Clamp(values[i + 3], 0, 255) << 8) | Math.Clamp(values[i + 4], 0, 255);
                        }

                        i += 4;
                    }

                    break;
            }
        }

        _style = new OutputStyle(foreground, flags);
    }
}
