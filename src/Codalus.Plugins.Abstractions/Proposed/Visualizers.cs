namespace Codalus.Plugins.Proposed;

/// <summary>
/// A debugger visualizer a plugin offers (M14b, ADR 0032): it appears in the View menu of the debugger's variable trees
/// for values of <see cref="Types"/>. Since API 0.2.7.
/// </summary>
/// <param name="Id">Unique within the plugin.</param>
/// <param name="Title">The menu item, e.g. "Dictionary Visualizer".</param>
/// <param name="Types">
/// Type names as the debugger shows them (<c>System.Collections.Generic.Dictionary&lt;string, int&gt;</c>); a name
/// ending in <c>*</c> matches as a prefix (<c>System.Collections.Generic.Dictionary&lt;*</c>).
/// </param>
public sealed record DebugVisualizerOptions(string Id, string Title, IReadOnlyList<string> Types);

/// <summary>The value being visualized, and evaluation in the paused program's selected frame.</summary>
public interface IDebugVisualizerContext
{
    /// <summary>The expression that evaluates to the value (use it to build further expressions).</summary>
    string Expression { get; }

    /// <summary>The value's type as the debugger shows it.</summary>
    string? Type { get; }

    /// <summary>The value as the debugger shows it.</summary>
    string Value { get; }

    /// <summary>
    /// Evaluates an expression in the paused program (netcoredbg: members, indexers and method calls; no <c>new</c>,
    /// casts or lambdas). Throws <see cref="InvalidOperationException"/> with the debugger's message when it fails.
    /// </summary>
    Task<DebugValue> EvaluateAsync(string expression, CancellationToken cancellationToken);
}

/// <summary>An evaluated value.</summary>
public sealed record DebugValue(string Value, string? Type);

/// <summary>What a visualizer shows: text in a read-only editor (colored by <see cref="LanguageId"/>) or a table.</summary>
public sealed record VisualizerView
{
    /// <summary>Text to show in a read-only editor.</summary>
    public string? Text { get; init; }

    /// <summary>The text's language (<c>json</c>, <c>xml</c>, <c>markdown</c>, …); null for plain text.</summary>
    public string? LanguageId { get; init; }

    /// <summary>A table's column headers.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>A table's rows, a cell per column.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? Rows { get; init; }

    /// <summary>Text, colored by <paramref name="languageId"/>.</summary>
    public static VisualizerView FromText(string text, string? languageId = null) => new() { Text = text, LanguageId = languageId };

    /// <summary>A table.</summary>
    public static VisualizerView FromTable(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows) => new() { Columns = columns, Rows = rows };
}
