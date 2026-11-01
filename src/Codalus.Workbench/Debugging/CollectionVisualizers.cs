using System.Globalization;
using System.Text;
using Codalus.Debugging;

namespace Codalus.Workbench.Debugging;

/// <summary>A collection's elements as rows: an index, the element's value, and an object's members as columns.</summary>
public sealed record DebugGrid(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);

/// <summary>
/// The grid and image visualizers (M14b), built from the adapter's own answers: netcoredbg evaluates
/// method calls but applies no type proxies, so a collection is read as an array (<c>.ToArray()</c>) and
/// a byte array as Base64.
/// </summary>
public static class CollectionVisualizers
{
    /// <summary>The most rows a grid shows.</summary>
    public const int MaxRows = 500;

    /// <summary>The most member columns a grid shows.</summary>
    public const int MaxColumns = 24;

    /// <summary>The largest image read from the debuggee.</summary>
    public const int MaxImageBytes = 16 * 1024 * 1024;

    private static readonly string[] CollectionTypes =
    [
        "System.Collections.Generic.List<", "System.Collections.Generic.Queue<", "System.Collections.Generic.Stack<",
        "System.Collections.Generic.HashSet<", "System.Collections.Generic.SortedSet<", "System.Collections.Immutable.ImmutableArray<",
        "System.Collections.Immutable.ImmutableList<", "System.Collections.ObjectModel.Collection<", "System.Collections.ObjectModel.ObservableCollection<",
        "System.Collections.ObjectModel.ReadOnlyCollection<", "System.Collections.ArrayList",
    ];

    public static bool IsArray(string? type) => type is not null && type.EndsWith(']') && type.Contains('[', StringComparison.Ordinal);

    public static bool IsByteArray(string? type) => type is "byte[]" or "System.Byte[]";

    /// <summary>
    /// Whether the grid can show a value of <paramref name="type"/>: arrays, the collections with an instance
    /// <c>ToArray()</c>, and Dictionary and HashSet through the data tip's collection views (D44, decision 5).
    /// </summary>
    public static bool IsCollection(string? type) =>
        IsArray(type) || (type is not null && CollectionTypes.Any(prefix => type.StartsWith(prefix, StringComparison.Ordinal))) || ThroughView(type) is not null;

    /// <summary>Dictionary and HashSet have no instance ToArray(): the grid reads them through their collection views.</summary>
    private static CollectionKind? ThroughView(string? type) => CollectionViews.KindOf(type) is CollectionKind.Dictionary or CollectionKind.HashSet ? CollectionViews.KindOf(type) : null;

    /// <summary>The expression that reads <paramref name="expression"/>'s elements as an array.</summary>
    public static string AsArray(string expression, string? type) => IsArray(type) ? expression : $"({expression}).ToArray()";

    /// <summary>The elements of the collection <paramref name="expression"/> evaluates to, with an object's members as columns.</summary>
    public static async Task<DebugGrid> GridAsync(DebugService debugger, string expression, string? type, CancellationToken cancellationToken = default)
    {
        if (ThroughView(type) is { } kind)
        {
            return await ViewGridAsync(debugger, expression, kind, cancellationToken).ConfigureAwait(false);
        }

        var array = await debugger.EvaluateAsync(AsArray(expression, type), "watch", cancellationToken).ConfigureAwait(false);
        if (array.VariablesReference == 0)
        {
            throw new InvalidOperationException($"'{expression}' has no elements to show ({array.Result}).");
        }

        var elements = (await debugger.VariablesAsync(array.VariablesReference).ConfigureAwait(false))
            .Where(e => e.Name.StartsWith('['))
            .ToList();
        var shown = elements.Take(MaxRows).ToList();
        var columns = new List<string>();
        var members = new List<IReadOnlyDictionary<string, string>>();
        foreach (var element in shown)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element.VariablesReference != 0 && !IsArray(element.Type))
            {
                foreach (var member in await debugger.VariablesAsync(element.VariablesReference).ConfigureAwait(false))
                {
                    // Skip the adapter's groups ("Static members") and indexers.
                    if (member.Name.Contains(' ', StringComparison.Ordinal) || member.Name.StartsWith('['))
                    {
                        continue;
                    }

                    if (!columns.Contains(member.Name) && columns.Count < MaxColumns)
                    {
                        columns.Add(member.Name);
                    }

                    values[member.Name] = member.Value;
                }
            }

            members.Add(values);
        }

        var rows = shown.Select((element, i) => (IReadOnlyList<string>)[
            element.Name.Trim('[', ']'),
            element.Value,
            .. columns.Select(c => members[i].TryGetValue(c, out var value) ? value : string.Empty)]).ToList();
        return new DebugGrid(["Index", "Value", .. columns], rows, elements.Count > shown.Count);
    }

    /// <summary>A dictionary's entries (Key, Value) or a set's elements (Index, Value), read by their collection view.</summary>
    private static async Task<DebugGrid> ViewGridAsync(DebugService debugger, string expression, CollectionKind kind, CancellationToken cancellationToken)
    {
        var value = await debugger.EvaluateAsync(expression, "watch", cancellationToken).ConfigureAwait(false);
        var raw = value.VariablesReference > 0 ? await debugger.VariablesAsync(value.VariablesReference).ConfigureAwait(false) : [];
        if (await CollectionViews.ReadAsync(kind, raw, r => debugger.VariablesAsync(r), (r, start, take) => debugger.VariablesAsync(r, new VariablesPage(start, take)), cancellationToken).ConfigureAwait(false) is not { } view)
        {
            throw new InvalidOperationException($"'{expression}' has no elements to show ({value.Result}).");
        }

        var shown = view.Elements.Take(MaxRows).ToList();
        var rows = kind == CollectionKind.Dictionary
            ? shown.Select(e => (IReadOnlyList<string>)[e.Children![0].Variable.Value, e.Variable.Value]).ToList()
            : shown.Select(e => (IReadOnlyList<string>)[e.Variable.Name.Trim('[', ']'), e.Variable.Value]).ToList();
        return new DebugGrid(kind == CollectionKind.Dictionary ? ["Key", "Value"] : ["Index", "Value"], rows, view.Count > shown.Count);
    }

    /// <summary>The bytes of the byte array <paramref name="expression"/> evaluates to.</summary>
    public static async Task<byte[]> BytesAsync(DebugService debugger, string expression, CancellationToken cancellationToken = default)
    {
        var base64 = await debugger.EvaluateAsync($"System.Convert.ToBase64String({expression})", "clipboard", cancellationToken).ConfigureAwait(false);
        var text = base64.Result.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
            if (text.Length > (MaxImageBytes / 3 * 4) + 4)
            {
                throw new InvalidOperationException($"The value is larger than {MaxImageBytes / (1024 * 1024)} MB.");
            }

            var bytes = new byte[text.Length * 3 / 4];
            if (Convert.TryFromBase64String(text, bytes, out var written))
            {
                return bytes[..written];
            }
        }

        // The adapter couldn't call Convert: read the elements one by one (small arrays only).
        var array = await debugger.EvaluateAsync(expression, "watch", cancellationToken).ConfigureAwait(false);
        if (array.VariablesReference == 0)
        {
            throw new InvalidOperationException($"'{expression}' isn't a byte array ({base64.Result}).");
        }

        var elements = await debugger.VariablesAsync(array.VariablesReference).ConfigureAwait(false);
        var result = new List<byte>(elements.Count);
        foreach (var element in elements.Where(e => e.Name.StartsWith('[')))
        {
            result.Add(byte.Parse(element.Value, NumberStyles.Integer, CultureInfo.InvariantCulture));
        }

        return [.. result];
    }

    /// <summary>The file extension of the image format <paramref name="bytes"/> start with, or null for anything else.</summary>
    public static string? ImageExtension(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
        [0xFF, 0xD8, 0xFF, ..] => ".jpg",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => ".gif",
        [(byte)'B', (byte)'M', ..] => ".bmp",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => ".webp",
        [0x00, 0x00, 0x01, 0x00, ..] => ".ico",
        _ => null,
    };

    /// <summary>The grid as a padded text table (cells cut at 60 characters).</summary>
    public static string Format(DebugGrid grid, string expression)
    {
        const int maxCell = 60;
        static string Cell(string value)
        {
            var line = value.ReplaceLineEndings(" ");
            return line.Length > maxCell ? line[..(maxCell - 1)] + "…" : line;
        }

        var cells = grid.Rows.Select(row => row.Select(Cell).ToList()).ToList();
        var widths = grid.Columns.Select((column, i) => Math.Max(column.Length, cells.Count == 0 ? 0 : cells.Max(row => row[i].Length))).ToList();
        var text = new StringBuilder();
        var count = grid.Rows.Count.ToString(CultureInfo.InvariantCulture);
        text.Append(expression).Append(" (").Append(grid.Truncated ? $"the first {count} elements" : count + (grid.Rows.Count == 1 ? " element" : " elements")).Append(')').AppendLine().AppendLine();
        void Row(IReadOnlyList<string> row)
        {
            var line = string.Join(" │ ", row.Select((cell, i) => i == 0 ? cell.PadLeft(widths[i]) : cell.PadRight(widths[i])));
            text.AppendLine(line.TrimEnd());
        }

        Row(grid.Columns);
        text.AppendJoin("─┼─", widths.Select(w => new string('─', w))).AppendLine();
        foreach (var row in cells)
        {
            Row(row);
        }

        return text.ToString();
    }
}
