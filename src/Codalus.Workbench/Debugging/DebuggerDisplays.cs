using System.Diagnostics;
using System.Text;
using Codalus.Debugging;
using Codalus.Debugging.Companion;

namespace Codalus.Workbench.Debugging;

/// <summary>
/// The debugger attributes, applied by the client (M14b, ADR 0032), since netcoredbg doesn't: an object's
/// <c>[DebuggerDisplay]</c> string is filled in by evaluating each <c>{expression}</c> on the object (netcoredbg calls
/// methods and property getters), and the members <c>[DebuggerBrowsable]</c> marks <c>Never</c> are left out and those
/// marked <c>RootHidden</c> are replaced by their children. The attributes come from the debuggee's assemblies through
/// the companion host. A display that can't be evaluated leaves netcoredbg's value as it was.
/// </summary>
public sealed class DebuggerDisplays
{
    /// <summary>Evaluations for one list of variables stop after this long: the rest keep netcoredbg's values.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(3);

    private const int MaxPerList = 60;

    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<TypeDisplayResult?>> _attributes;
    private readonly Dictionary<string, Task<TypeDisplayResult?>> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _referenceTypes = [];

    /// <param name="attributes">The attributes of a type in the given modules (the companion host), or null without them.</param>
    public DebuggerDisplays(Func<string, IReadOnlyList<string>, CancellationToken, Task<TypeDisplayResult?>> attributes) => _attributes = attributes;

    /// <summary>Variable references don't survive a resume: forget them (the attributes stay known for the session).</summary>
    public void Resumed()
    {
        lock (_referenceTypes)
        {
            _referenceTypes.Clear();
        }
    }

    public void SessionEnded()
    {
        Resumed();
        lock (_types)
        {
            _types.Clear();
        }
    }

    /// <summary>A variables list with the attributes applied: hidden members out, root-hidden ones flattened, displays filled.</summary>
    public async Task<IReadOnlyList<Variable>> ApplyAsync(
        int parentReference,
        IReadOnlyList<Variable> variables,
        IReadOnlyList<string> modules,
        Func<int, Task<IReadOnlyList<Variable>>> children,
        Func<string, CancellationToken, Task<EvaluateResponse>> evaluate,
        CancellationToken cancellationToken)
    {
        var result = new List<Variable>(variables.Count);
        TypeDisplayResult? parent = null;
        string? parentType;
        lock (_referenceTypes)
        {
            _referenceTypes.TryGetValue(parentReference, out parentType);
        }

        if (parentType is not null)
        {
            parent = await TypeAsync(parentType, modules, cancellationToken).ConfigureAwait(false);
        }

        foreach (var variable in variables)
        {
            if (parent is not null && parent.Hidden.Contains(variable.Name))
            {
                continue;
            }

            if (parent is not null && parent.RootHidden.Contains(variable.Name) && variable.VariablesReference > 0)
            {
                result.AddRange(await children(variable.VariablesReference).ConfigureAwait(false));
                continue;
            }

            result.Add(variable);
        }

        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < result.Count && i < MaxPerList; i++)
        {
            var variable = result[i];
            if (variable.VariablesReference > 0 && variable.Type is { } type)
            {
                Remember(variable.VariablesReference, type);
            }

            if (Stopwatch.GetElapsedTime(started) > Budget || !IsObjectValue(variable.Value, variable.Type) || variable.EvaluateName is not { } expression)
            {
                continue;
            }

            if (await DisplayAsync(variable.Type!, expression, modules, evaluate, cancellationToken).ConfigureAwait(false) is { } display)
            {
                result[i] = variable with { Value = display };
            }
        }

        return result;
    }

    /// <summary>An evaluation's result with the type's display (Watch, QuickWatch, data tips).</summary>
    public async Task<EvaluateResponse> ApplyAsync(
        string expression,
        EvaluateResponse response,
        IReadOnlyList<string> modules,
        Func<string, CancellationToken, Task<EvaluateResponse>> evaluate,
        CancellationToken cancellationToken)
    {
        if (response.VariablesReference > 0 && response.Type is { } type)
        {
            Remember(response.VariablesReference, type);
        }

        return IsObjectValue(response.Result, response.Type)
            && await DisplayAsync(response.Type!, expression, modules, evaluate, cancellationToken).ConfigureAwait(false) is { } display
            ? response with { Result = display }
            : response;
    }

    private void Remember(int reference, string type)
    {
        lock (_referenceTypes)
        {
            _referenceTypes[reference] = type;
        }
    }

    /// <summary>netcoredbg shows an object as <c>{Namespace.Type}</c>; arrays (<c>{int[3]}</c>) have no attributes.</summary>
    internal static bool IsObjectValue(string value, string? type) =>
        type is not null && value.Length > 2 && value[0] == '{' && value[^1] == '}' && !value.EndsWith("]}", StringComparison.Ordinal);

    private async Task<string?> DisplayAsync(
        string type,
        string expression,
        IReadOnlyList<string> modules,
        Func<string, CancellationToken, Task<EvaluateResponse>> evaluate,
        CancellationToken cancellationToken)
    {
        if (await TypeAsync(type, modules, cancellationToken).ConfigureAwait(false) is not { Display: { } format })
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var part in Parse(format))
        {
            if (part.Expression is null)
            {
                text.Append(part.Text);
                continue;
            }

            EvaluateResponse value;
            try
            {
                value = await evaluate(Member(expression, part.Expression), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DapException or InvalidOperationException or TimeoutException)
            {
                return null; // an expression the debugger can't evaluate: keep netcoredbg's value
            }

            text.Append(part.NoQuotes && value.Result is ['"', .., '"'] quoted ? quoted[1..^1] : value.Result);
        }

        return text.ToString();
    }

    /// <summary>The expression applied to the object: <c>{Name}</c> on <c>order</c> is <c>(order).Name</c>.</summary>
    internal static string Member(string target, string member) => $"({target}).{member}";

    private Task<TypeDisplayResult?> TypeAsync(string type, IReadOnlyList<string> modules, CancellationToken cancellationToken)
    {
        lock (_types)
        {
            if (!_types.TryGetValue(type, out var known))
            {
                known = LookUpAsync(type, modules, cancellationToken);
                _types[type] = known;
            }

            return known;
        }
    }

    private async Task<TypeDisplayResult?> LookUpAsync(string type, IReadOnlyList<string> modules, CancellationToken cancellationToken)
    {
        try
        {
            return await _attributes(type, modules, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return null; // no companion host: netcoredbg's values as they are
        }
    }

    internal sealed record Part(string Text, string? Expression, bool NoQuotes);

    /// <summary>
    /// <c>Order {Id}: {Customer,nq}</c> → text, <c>Id</c>, text, <c>Customer</c> without quotes. Format specifiers after a
    /// comma (<c>nq</c>, <c>h</c>, <c>d</c>) are taken off the expression; only <c>nq</c> changes the result.
    /// </summary>
    internal static IReadOnlyList<Part> Parse(string format)
    {
        var parts = new List<Part>();
        var text = new StringBuilder();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '\\' && i + 1 < format.Length && format[i + 1] is '{' or '}')
            {
                text.Append(format[++i]);
                continue;
            }

            if (format[i] != '{')
            {
                text.Append(format[i]);
                continue;
            }

            var depth = 1;
            var end = i + 1;
            while (end < format.Length && depth > 0)
            {
                depth += format[end] switch { '{' => 1, '}' => -1, _ => 0 };
                end++;
            }

            if (depth != 0)
            {
                text.Append(format[i..]);
                break;
            }

            if (text.Length > 0)
            {
                parts.Add(new Part(text.ToString(), null, false));
                text.Clear();
            }

            var inner = format[(i + 1)..(end - 1)].Trim();
            var specifiers = Specifiers(inner, out var expression);
            parts.Add(new Part(string.Empty, expression, specifiers.Contains("nq")));
            i = end - 1;
        }

        if (text.Length > 0)
        {
            parts.Add(new Part(text.ToString(), null, false));
        }

        return parts;
    }

    /// <summary>The format specifiers after the expression's last top-level comma (<c>Customer,nq</c>).</summary>
    private static string[] Specifiers(string inner, out string expression)
    {
        var depth = 0;
        for (var i = inner.Length - 1; i >= 0; i--)
        {
            depth += inner[i] switch { ')' or ']' => 1, '(' or '[' => -1, _ => 0 };
            if (inner[i] == ',' && depth == 0 && inner[(i + 1)..].Trim() is var tail && tail.All(c => char.IsLetter(c) || c == ',' || c == ' '))
            {
                expression = inner[..i].Trim();
                return tail.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
        }

        expression = inner;
        return [];
    }
}
