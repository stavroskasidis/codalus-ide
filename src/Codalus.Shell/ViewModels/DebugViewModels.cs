using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.FileSystem;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Output;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// Where a debugger tree's children come from (the data tip, Locals, Autos, Watch and QuickWatch share it): the adapter's
/// variables of a node's reference, as rows (D44): each member's kind from the companion's member list, a library
/// type's non-public members under Non-Public members, the solution's own inline with a lock badge, netcoredbg's Static
/// members last; a getter that threw as "threw Type: message", and netcoredbg's <c>&lt;error&gt;</c> as "Couldn't
/// evaluate" with Re-evaluate. Without a debugger (or its member lists), the kinds are guessed from the names.
/// </summary>
/// <remarks>
/// A long array shows its elements in ranges (M56, the maintainer's choice): up to 1,000 show directly; a longer one
/// shows [0..999], [1000..1999]… that read their elements with a page of the adapter's variables when expanded; over
/// 1,000,000 the ranges nest, ranges of 1,000,000 holding ranges of 1,000. An adapter that sends more than 1,000
/// children unasked (one that doesn't page, or doesn't count) shows the first 1,000 and a row that says how many more.
/// The rows are built off the UI thread; each node's children change at once (<see cref="VariableNode.Children"/>).
/// </remarks>
public class VariableSource
{
    /// <summary>The most children a node shows at once (M56).</summary>
    public const int PageSize = 1000;

    /// <summary>Over this many elements, an array's ranges nest (M56).</summary>
    public const int NestedPageSize = 1_000_000;

    private readonly Func<int, VariablesPage?, Task<IReadOnlyList<Variable>>> _variables;

    public VariableSource(Func<int, Task<IReadOnlyList<Variable>>> variables) => _variables = (r, _) => variables(r);

    public VariableSource(Func<int, VariablesPage?, Task<IReadOnlyList<Variable>>> variables) => _variables = variables;

    public VariableSource(DebugService debugger)
        : this((r, page) => debugger.VariablesAsync(r, page)) => Debugger = debugger;

    protected DebugService? Debugger { get; }

    /// <summary>The children of <paramref name="parent"/>, read from its variables reference (on any thread: they're built off the UI thread).</summary>
    public virtual async Task<IReadOnlyList<VariableNode>> ChildrenAsync(VariableNode parent, CancellationToken cancellationToken)
    {
        if (parent.Kind == MemberKind.Range)
        {
            return await ElementsAsync(parent, parent.RangeStart, parent.Counts.Indexed ?? 0, cancellationToken).ConfigureAwait(false);
        }

        if (parent.Counts.Indexed is > 0 and var count)
        {
            // An array: its named children (a script's array has some), then its elements, or their ranges.
            IReadOnlyList<VariableNode> named = parent.Counts is { Filter: not null, Named: > 0 and var namedCount }
                ? await NodesAsync(VariableGrouping.Group(await PageAsync(parent.Reference, new VariablesPage(0, namedCount, "named")).ConfigureAwait(false), null, _ => false), parent, parent.Reference, cancellationToken).ConfigureAwait(false)
                : [];
            return [.. named, .. await ElementsAsync(parent, 0, count, cancellationToken).ConfigureAwait(false)];
        }

        var variables = await _variables(parent.Reference, null).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var more = variables.Count - PageSize;
        if (more > 0)
        {
            variables = [.. variables.Take(PageSize)];
        }

        IReadOnlyList<DebugMember> rows;
        if (Debugger is not { } debugger)
        {
            rows = VariableGrouping.Group(variables, null, _ => false);
        }
        else if (parent.Kind == MemberKind.StaticGroup)
        {
            // netcoredbg's Static members node: its evaluate name is the type's.
            rows = VariableGrouping.Statics(variables, await debugger.MembersAsync(parent.EvaluateName ?? string.Empty, cancellationToken).ConfigureAwait(false), debugger.IsUserCode);
        }
        else
        {
            rows = VariableGrouping.Group(variables, await debugger.MembersAsync(parent.Type ?? string.Empty, cancellationToken).ConfigureAwait(false), debugger.IsUserCode);
        }

        // List, Dictionary, HashSet, Queue and Stack: their elements, read from their fields, then Raw View with their own
        // members (decision 1); fields no table knows leave the members as they are.
        if (parent.Kind != MemberKind.RawView && CollectionViews.KindOf(parent.Type) is { } kind
            && await CollectionViews.ReadAsync(kind, variables, r => _variables(r, null), (r, start, take) => _variables(r, new VariablesPage(start, take)), cancellationToken).ConfigureAwait(false) is { } view)
        {
            rows = [.. view.Elements, new DebugMember(new Variable("Raw View", string.Empty, parent.Type, parent.Reference), MemberKind.RawView) { Children = rows }];
        }

        var nodes = await NodesAsync(rows, parent, parent.Reference, cancellationToken).ConfigureAwait(false);
        return more > 0 ? [.. nodes, VariableNode.More(more)] : nodes;
    }

    /// <summary>
    /// <paramref name="count"/> elements of <paramref name="parent"/>'s array from <paramref name="start"/>: a page of them,
    /// or the ranges they make when there are more than <see cref="PageSize"/> (of <see cref="NestedPageSize"/> past that).
    /// </summary>
    private async Task<IReadOnlyList<VariableNode>> ElementsAsync(VariableNode parent, int start, int count, CancellationToken cancellationToken)
    {
        if (count > PageSize)
        {
            // By the range's index: an array near Array.MaxLength elements has more than int.MaxValue - 1,000,000 of them.
            var size = count > NestedPageSize ? NestedPageSize : PageSize;
            var number = (int)(((long)count + size - 1) / size);
            var ranges = new List<VariableNode>(number);
            for (var i = 0; i < number; i++)
            {
                var at = start + (i * size);
                var length = Math.Min(size, count - (i * size));
                ranges.Add(new VariableNode(string.Create(CultureInfo.InvariantCulture, $"[{at}..{at + length - 1}]"), string.Empty, null, parent.Reference, this)
                {
                    Kind = MemberKind.Range,
                    Icon = "symbol-raw-view",
                    Parent = parent,
                    RangeStart = at,
                    Counts = new ChildCounts(length, null, parent.Counts.Filter),
                });
            }

            return ranges;
        }

        // netcoredbg's short array at once, as before; a range, or an adapter that counts DAP's way, a page at a time.
        var page = parent.Kind != MemberKind.Range && parent.Counts.Filter is null ? null : new VariablesPage(start, count, parent.Counts.Filter);
        var variables = page is null ? await _variables(parent.Reference, null).ConfigureAwait(false) : await PageAsync(parent.Reference, page).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var more = variables.Count - PageSize;
        var nodes = await NodesAsync(VariableGrouping.Group(more > 0 ? [.. variables.Take(PageSize)] : variables, null, _ => false), parent, parent.Reference, cancellationToken).ConfigureAwait(false);
        return more > 0 ? [.. nodes, VariableNode.More(more)] : nodes;
    }

    /// <summary>A page of a reference's children; an adapter that sent more (it doesn't page) gives the page's own.</summary>
    private async Task<IReadOnlyList<Variable>> PageAsync(int reference, VariablesPage page)
    {
        var variables = await _variables(reference, page).ConfigureAwait(false);
        if (variables.Count <= page.Count)
        {
            return variables;
        }

        return variables.Count >= page.Start + page.Count ? [.. variables.Skip(page.Start).Take(page.Count)] : [.. variables.Take(page.Count)];
    }

    /// <summary>The rows' nodes under <paramref name="parent"/>; <paramref name="container"/> is the reference the adapter's variables are in (setVariable's).</summary>
    protected async Task<IReadOnlyList<VariableNode>> NodesAsync(IReadOnlyList<DebugMember> rows, VariableNode parent, int container, CancellationToken cancellationToken)
    {
        var nodes = new List<VariableNode>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested(); // a load nobody waits for anymore builds no more nodes, nor asks what threw
            nodes.Add(await NodeAsync(row, parent, container, nodes.Count, cancellationToken).ConfigureAwait(false));
        }

        return nodes;
    }

    private async Task<VariableNode> NodeAsync(DebugMember row, VariableNode parent, int container, int index, CancellationToken cancellationToken)
    {
        var variable = row.Variable;
        var value = variable.Value;
        var error = false;
        var reevaluate = false;
        if (row.Threw)
        {
            value = await ThrewAsync(variable).ConfigureAwait(false);
            error = true;
        }
        else if (row.CouldNotEvaluate)
        {
            value = Localizer.Get("dataTip.couldntEvaluate", "Couldn't evaluate");
            error = true;
            reevaluate = variable.EvaluateName is not null;
        }

        var group = row.Kind is MemberKind.NonPublicGroup or MemberKind.StaticGroup or MemberKind.RawView;
        int? set = group || row.Container == DebugMember.ByExpression ? null : row.Container ?? container;
        var node = new VariableNode(Label(row), value, group ? null : variable.Type, row.Kind == MemberKind.RawView ? 0 : variable.VariablesReference, this, set, group && row.Kind != MemberKind.StaticGroup ? null : variable.EvaluateName)
        {
            Icon = IconOf(row),
            Kind = row.Kind,
            HasBadge = row.IsNonPublic && row.IsUserCode,
            CanWrite = row.CanWrite,
            IsDuplicate = row.IsDuplicate,
            IsError = error,
            CanReevaluate = reevaluate,
            Description = Describe(row),
            Parent = parent,
            IsDisplayPending = row.Kind == MemberKind.Element && Debugger?.Displays is not null && index >= DebuggerDisplays.MaxPerList
                && DebuggerDisplays.IsObjectValue(variable.Value, variable.Type) && variable.EvaluateName is not null,
            Counts = group ? default : ChildCounts.Of(variable),
        };
        if (row.Children is { } children)
        {
            node.SetFixedChildren(await NodesAsync(children, node, container, cancellationToken).ConfigureAwait(false));
        }

        Track(node);
        return node;
    }

    /// <summary>
    /// The [DebuggerDisplay] of an element that has scrolled into view (A10): the display is applied to the first 60 of a
    /// list as it loads, and to the rest as they're shown.
    /// </summary>
    public virtual async Task DisplayAsync(VariableNode node)
    {
        if (Debugger is not { } debugger || node.EvaluateName is not { } expression || !node.IsDisplayPending)
        {
            return;
        }

        node.IsDisplayPending = false;
        try
        {
            var result = await debugger.EvaluateAsync(expression, "watch").ConfigureAwait(true);
            node.SetValue(result.Result, result.Type);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            // netcoredbg's value stays.
        }
    }

    /// <summary>Notes a node's value at this stop, and marks it changed when it differs from the last stop's (decision 12).</summary>
    public void Track(VariableNode node)
    {
        if (Debugger is { State: DebugState.Stopped } debugger && node.EvaluateName is { } expression && !node.IsPlaceholder && !node.IsGroup)
        {
            node.IsChanged = !node.IsError && debugger.Values.Record(expression, node.Value);
        }
    }

    /// <summary>Re-evaluates a value netcoredbg couldn't (Re-evaluate on "Couldn't evaluate").</summary>
    public virtual async Task ReevaluateAsync(VariableNode node)
    {
        if (Debugger is not { } debugger || node.EvaluateName is not { } expression)
        {
            return;
        }

        try
        {
            var result = await debugger.EvaluateAsync(expression, "watch").ConfigureAwait(true);
            if (result.Result != VariableGrouping.Error)
            {
                node.SetError(false, canReevaluate: false);
                node.Update(result.Result, result.Type ?? node.Type, result.VariablesReference, ChildCounts.Of(result.Type ?? node.Type, result.NamedVariables, result.IndexedVariables));
            }
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            // Still can't: the row keeps saying so.
        }
    }

    /// <summary>"threw InvalidOperationException: Session has not been configured…", from the exception's Message.</summary>
    private async Task<string> ThrewAsync(Variable variable)
    {
        var message = string.Empty;
        if (variable.VariablesReference > 0)
        {
            try
            {
                var members = await _variables(variable.VariablesReference, null).ConfigureAwait(false);
                message = (members.FirstOrDefault(m => m.Name == "Message") ?? members.FirstOrDefault(m => m.Name is "_message" or "message"))?.Value ?? string.Empty;
            }
            catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException)
            {
                // The type alone.
            }
        }

        var type = VariableGrouping.ShortName(variable.Type ?? string.Empty);
        return message.Length > 0
            ? Localizer.Format("dataTip.threwMessage", "threw {0}: {1}", type, Visualizers.Unquote(message))
            : Localizer.Format("dataTip.threw", "threw {0}", type);
    }

    private static string Label(DebugMember row) => row.Kind switch
    {
        MemberKind.NonPublicGroup => Localizer.Get("dataTip.nonPublicMembers", "Non-Public members"),
        MemberKind.StaticGroup => Localizer.Get("dataTip.staticMembers", "Static members"),
        MemberKind.RawView => Localizer.Get("dataTip.rawView", "Raw View"),
        _ => row.Variable.Name,
    };

    public static string IconOf(DebugMember row) => row.Kind switch
    {
        MemberKind.Property => "symbol-property",
        MemberKind.Constant => "symbol-constant",
        MemberKind.Event => "symbol-event",
        MemberKind.NonPublicGroup => "lock",
        MemberKind.StaticGroup => "symbol-class",
        MemberKind.RawView => "symbol-raw-view",
        MemberKind.Unknown => VariableIcons.Guess(row.Variable.Name),
        _ => "symbol-field",
    };

    /// <summary>The name's tooltip before the type, for a non-public member: "Private field of Controller (your code)".</summary>
    private static string? Describe(DebugMember row)
    {
        if (!row.IsNonPublic || row.Access is not { } access || row.DeclaringType is not { } declaring)
        {
            return null;
        }

        var what = (access, row.Kind) switch
        {
            ("private", MemberKind.Property) => Localizer.Get("dataTip.privateProperty", "Private property"),
            ("private", _) => Localizer.Get("dataTip.privateField", "Private field"),
            ("protected", MemberKind.Property) => Localizer.Get("dataTip.protectedProperty", "Protected property"),
            ("protected", _) => Localizer.Get("dataTip.protectedField", "Protected field"),
            (_, MemberKind.Property) => Localizer.Get("dataTip.internalProperty", "Internal property"),
            _ => Localizer.Get("dataTip.internalField", "Internal field"),
        };
        var owner = VariableGrouping.ShortName(declaring);
        return row.IsUserCode
            ? Localizer.Format("dataTip.memberOfYourCode", "{0} of {1} (your code)", what, owner)
            : Localizer.Format("dataTip.memberOf", "{0} of {1}", what, owner);
    }
}

/// <summary>The icon of a row whose kind nobody reported: guessed from its name, as for a remote program (B3).</summary>
public static class VariableIcons
{
    public static string Guess(string name) => VariableGrouping.Guess(name) switch
    {
        MemberKind.Property => "symbol-property",
        _ => "symbol-field",
    };
}

/// <summary>
/// Which rows of a variable window are expanded, by evaluate name (D44, decision 9): Locals and Autos build new rows at
/// every stop, Watch reloads a changed value's members, and both open again what was open.
/// </summary>
public sealed class ExpansionMemory
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    /// <summary>A row's key: its evaluate name, else its parent's key and its name (Non-Public members, Raw View).</summary>
    public static string Key(VariableNode node) =>
        node.EvaluateName is { } expression && !node.IsGroup ? expression : (node.Parent is { } parent ? Key(parent) + "/" : string.Empty) + node.Name;

    public bool IsExpanded(VariableNode node) => _expanded.Contains(Key(node));

    public void Set(VariableNode node, bool expanded)
    {
        if (expanded)
        {
            _expanded.Add(Key(node));
        }
        else
        {
            _expanded.Remove(Key(node));
        }
    }
}

/// <summary>
/// How many children the adapter counts for a value (M56): an array's elements are <see cref="Indexed"/>, DAP's
/// <c>indexedVariables</c> (read with <see cref="Filter"/> <c>indexed</c>), or netcoredbg's <c>namedVariables</c> of
/// an array (read without a filter); <see cref="Named"/> are the other children of an array that has both.
/// </summary>
public readonly record struct ChildCounts(int? Indexed, int? Named, string? Filter)
{
    public static ChildCounts Of(Variable variable) => Of(variable.Type, variable.NamedVariables, variable.IndexedVariables);

    public static ChildCounts Of(EvaluateResponse result) => Of(result.Type, result.NamedVariables, result.IndexedVariables);

    public static ChildCounts Of(string? type, int? named, int? indexed) =>
        indexed is > 0 ? new(indexed, named, "indexed")
        : named is > 0 && type is { } t && t.EndsWith(']') ? new(named, null, null)
        : default;
}

/// <summary>A variable (or watch, or evaluated expression) in a debugger tree; children load when it is expanded.</summary>
#pragma warning disable CA1001 // _loading is only cancelled (a newer load, or a new value), never waited on: no handle to free.
public sealed class VariableNode : ObservableObject
#pragma warning restore CA1001
{
    private readonly VariableSource? _source;
    private CancellationTokenSource? _loading;
    private ChildCounts _counts;
    private bool _isExpanded;
    private bool _loaded;
    private string _value;
    private string? _type;
    private int _reference;
    private bool _fixed;
    private bool _isError;
    private bool _canReevaluate;
    private bool _displayPending;
    private bool _isChanged;

    public VariableNode(string name, string value, string? type, int reference, VariableSource? source, int? parentReference = null, string? evaluateName = null)
    {
        Name = name;
        EvaluateName = evaluateName;
        _value = value;
        _type = type;
        _reference = reference;
        _source = source;
        ParentReference = parentReference;
        if (reference > 0 && source is not null)
        {
            Children.Add(Placeholder());
        }
    }

    private static VariableNode Placeholder() => new(string.Empty, Localizer.Get("dataTip.loading", "Loading…"), null, 0, null) { IsPlaceholder = true };

    /// <summary>The last row of a list cut at <see cref="VariableSource.PageSize"/>: how many more there are (M56), muted as "Loading…" is.</summary>
    public static VariableNode More(int count) =>
        new(string.Empty, Localizer.Format("dataTip.more", "{0} more", count.ToString("N0", CultureInfo.CurrentCulture)), null, 0, null) { IsPlaceholder = true };

    /// <summary>How many children the adapter counts (an array's elements: over 1,000 they show in ranges, M56).</summary>
    public ChildCounts Counts
    {
        get => _counts;
        init => _counts = value;
    }

    /// <summary>A range's first element (<see cref="MemberKind.Range"/>; its <see cref="Counts"/> are its length).</summary>
    public int RangeStart { get; init; }

    public string Name { get; }

    /// <summary>The expression that evaluates to this value (the adapter's <c>evaluateName</c>; a watch's own expression).</summary>
    public string? EvaluateName { get; }

    /// <summary>The node this one is a child of (null for a root).</summary>
    public VariableNode? Parent { get; init; }

    /// <summary>The row's icon kind (a code symbol: <c>symbol-property</c>, <c>symbol-field</c>…).</summary>
    public string Icon { get; init; } = "symbol-local";

    /// <summary>What the row is (D44): a property, a field, a group…</summary>
    public MemberKind Kind { get; init; }

    /// <summary>A non-public member of the solution's own code: its icon carries the lock badge.</summary>
    public bool HasBadge { get; init; }

    /// <summary>A node the shell adds (Non-Public members, Static members, Raw View, an array's range): no value of its own, no pin.</summary>
    public bool IsGroup => Kind is MemberKind.NonPublicGroup or MemberKind.StaticGroup or MemberKind.RawView or MemberKind.Range;

    /// <summary>False when the member list says the value can't be set (no setter, read-only); null when nothing says.</summary>
    public bool? CanWrite { get; init; }

    /// <summary>netcoredbg's "name (Owner)" for a member a derived type hides (no evaluate name).</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>"Couldn't evaluate": Re-evaluate asks again.</summary>
    public bool CanReevaluate
    {
        get => _canReevaluate;
        init => _canReevaluate = value;
    }

    /// <summary>An element past the first 60 of a list, its [DebuggerDisplay] not applied yet: muted until it scrolls into view (A10).</summary>
    public bool IsDisplayPending
    {
        get => _displayPending;
        set => SetField(ref _displayPending, value);
    }

    /// <summary>Applies the element's [DebuggerDisplay] now that it shows.</summary>
    public Task ResolveDisplayAsync() => IsDisplayPending && _source is not null ? _source.DisplayAsync(this) : Task.CompletedTask;

    /// <summary>What a non-public member is, before its type in the name's tooltip ("Private field of Controller (your code)").</summary>
    public string? Description { get; init; }

    /// <summary>The muted "Loading…" row shown while the children load (and the "1,000 more" row of a list cut short).</summary>
    public bool IsPlaceholder { get; private init; }

    /// <summary>Whether the value is a string (visualizers apply).</summary>
    public bool IsString => _type is "string" or "System.String" && _value.StartsWith('"');

    /// <summary>The variables reference of the container (for setting this variable's value).</summary>
    public int? ParentReference { get; }

    /// <summary>The adapter's reference to this value's members (0: none).</summary>
    public int Reference => _reference;

    public string Value
    {
        get => _value;
        private set
        {
            if (SetField(ref _value, value))
            {
                _source?.Track(this);
            }
        }
    }

    /// <summary>Whether the value differs from its value at the last stop (by evaluate name): drawn in red (decision 12).</summary>
    public bool IsChanged
    {
        get => _isChanged;
        set => SetField(ref _isChanged, value);
    }

    /// <summary>The source the node's children come from (pinned tips and the windows track their roots' changes through it).</summary>
    public VariableSource? Source => _source;

    public string? Type
    {
        get => _type;
        private set
        {
            if (SetField(ref _type, value))
            {
                OnPropertyChanged(nameof(HasType));
                OnPropertyChanged(nameof(NameTip));
            }
        }
    }

    public bool HasType => !string.IsNullOrEmpty(_type);

    /// <summary>Whether the value has members to expand (a chevron, a child tip).</summary>
    public bool HasChildren => _fixed || (_reference > 0 && _source is not null);

    /// <summary>The name's tooltip: what a non-public member is, and the type the adapter reports (a data tip has no type column).</summary>
    public string? NameTip => Description is { } description ? (HasType ? $"{description} · {Type}" : description) : HasType ? Type : null;

    public bool IsError
    {
        get => _isError;
        init => _isError = value;
    }

    /// <summary>Shows the value as an error or not (Re-evaluate's answer).</summary>
    public void SetError(bool error, bool canReevaluate)
    {
        if (_isError != error)
        {
            _isError = error;
            OnPropertyChanged(nameof(IsError));
        }

        if (_canReevaluate != canReevaluate)
        {
            _canReevaluate = canReevaluate;
            OnPropertyChanged(nameof(CanReevaluate));
        }
    }

    /// <summary>Asks again for a value netcoredbg couldn't evaluate.</summary>
    public Task ReevaluateAsync() => _source?.ReevaluateAsync(this) ?? Task.CompletedTask;

    /// <summary>A group's members, known already (Non-Public members, a collection view's Raw View).</summary>
    public void SetFixedChildren(IReadOnlyList<VariableNode> children)
    {
        _fixed = true;
        _loaded = true;
        Children.Reset(children);
        OnPropertyChanged(nameof(HasChildren));
    }

    /// <summary>
    /// The children, changed all at once (M56): a reset per load, not an event per child, each of which rebuilt every row
    /// of an open data tip (a list of 1,000 built half a million rows).
    /// </summary>
    public ResettableCollection<VariableNode> Children { get; } = [];

    public string AccessibleName => HasType ? $"{Name} = {Value} ({Type})" : $"{Name} = {Value}";

    /// <summary>Shows a value set from outside (an edit through another node).</summary>
    public void SetValue(string value, string? type)
    {
        Value = value;
        Type = type ?? Type;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value) && value && !_loaded)
            {
                _ = LoadChildrenAsync();
            }
        }
    }

    /// <summary>The children, loaded once (a child tip opening; what's loading is awaited).</summary>
    public Task LoadChildrenOnceAsync() => _loaded ? Task.CompletedTask : LoadChildrenAsync();

    /// <summary>A new value for this node (a re-evaluated watch, a changed variable): reloads its children if expanded.</summary>
    public void Update(string value, string? type, int reference, ChildCounts counts = default)
    {
        Value = value;
        Type = type;
        if (_fixed || (reference == _reference && _loaded && counts == _counts))
        {
            return;
        }

        _reference = reference;
        _counts = counts;
        _loaded = false;
        _loading?.Cancel();
        _loading = null;
        foreach (var child in Children)
        {
            child.CancelLoads(); // what's still loading under the children this value replaces
        }

        Children.Reset(reference > 0 && _source is not null ? [Placeholder()] : []);
        OnPropertyChanged(nameof(HasChildren));
        if (reference > 0 && _source is not null && _isExpanded)
        {
            _ = LoadChildrenAsync();
        }
    }

    /// <summary>
    /// Stops loading the children of this node and of what's loaded under it: its rows went (a new stop's replaced them,
    /// M56). A load nobody waited for anymore went on building nodes for rows that were gone.
    /// </summary>
    public void CancelLoads()
    {
        if (_loading is { } loading)
        {
            loading.Cancel();
            _loading = null;
            _loaded = false;
        }

        foreach (var child in Children)
        {
            child.CancelLoads();
        }
    }

    private async Task LoadChildrenAsync()
    {
        if (_source is null || _reference <= 0)
        {
            return;
        }

        _loaded = true;
        _loading?.Cancel();
        var loading = _loading = new CancellationTokenSource();
        try
        {
            // Read and built off the UI thread (M56): up to a thousand rows' nodes, their grouping and their member lists.
            var source = _source;
            var children = await Task.Run(() => source.ChildrenAsync(this, loading.Token), loading.Token).ConfigureAwait(true);
            if (loading.IsCancellationRequested)
            {
                return;
            }

            Children.Reset(children);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            if (!loading.IsCancellationRequested)
            {
                Children.Reset([new VariableNode(string.Empty, ex.Message, null, 0, null) { IsError = true, Icon = "error" }]);
            }
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                _loading = null;
            }
        }
    }
}

/// <summary>
/// Locals or Autos: the selected frame's variables, refreshed on every stop and frame change. Autos, often the tab
/// behind Locals, reads them only while it's shown, and once when it's shown (M56).
/// </summary>
/// <remarks>
/// TODO(issue): Locals only while shown too: the shell shows it at a session's first stop, and later sessions' stops
/// (the smoke test's, DebugSessionsTests') read it while another window is in front of it.
/// </remarks>
public sealed class VariablesViewModel : ObservableObject, IShownAware
{
    public const string LocalsWindowId = "debug.locals";
    public const string AutosWindowId = "debug.autos";

    private readonly DebugService _debugger;
    private readonly Func<string, int, string?> _lineText;
    private readonly VariableSource _source;
    private readonly ShownRefresh _refresh;
    private bool _isEmpty = true;
    private int _generation;

    public VariablesViewModel(DebugService debugger, bool autos, Func<string, int, string?> lineText)
    {
        _debugger = debugger;
        _source = new VariableSource(debugger);
        IsAutos = autos;
        _lineText = lineText;
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(Refresh);
        debugger.FrameChanged += (_, _) => Ui.Post(Refresh);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State != DebugState.Stopped)
            {
                Clear();
            }
        });
    }

    public bool IsAutos { get; }

    private void Refresh()
    {
        if (IsAutos)
        {
            _refresh.Request();
        }
        else
        {
            _ = RefreshAsync();
        }
    }

    void IShownAware.SetViewShown(bool shown)
    {
        if (IsAutos)
        {
            _refresh.SetShown(shown);
        }
    }

    /// <summary>The frame's variables, replaced at once at each stop (M56).</summary>
    public ResettableCollection<VariableNode> Roots { get; } = [];

    /// <summary>What's expanded, by evaluate name: opened again at the next stop (D44, decision 9).</summary>
    public ExpansionMemory Expansion { get; } = new();

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetField(ref _isEmpty, value);
    }

    private void Clear()
    {
        _generation++;
        Replace([]);
        IsEmpty = true;
    }

    /// <summary>Shows <paramref name="roots"/>; what's still loading under the rows they replace stops (M56).</summary>
    private void Replace(IReadOnlyList<VariableNode> roots)
    {
        foreach (var old in Roots)
        {
            old.CancelLoads();
        }

        Roots.Reset(roots);
    }

    public async Task RefreshAsync()
    {
        _refresh.Fresh();
        var generation = ++_generation;
        if (_debugger.State != DebugState.Stopped || _debugger.SelectedFrame is not { } frame)
        {
            Clear();
            return;
        }

        try
        {
            var scopes = await _debugger.ScopesAsync(frame).ConfigureAwait(true);
            var variables = new List<Variable>();
            foreach (var scope in scopes.Where(s => !s.Expensive))
            {
                variables.AddRange(await _debugger.VariablesAsync(scope.VariablesReference).ConfigureAwait(true));
            }

            if (IsAutos)
            {
                var names = AutosNames(frame);
                variables = [.. variables.Where(v => names.Contains(v.Name) || v.Name == "this")];
            }

            if (generation != _generation)
            {
                return;
            }

            var reference = scopes.Count > 0 ? scopes[0].VariablesReference : 0;
            var roots = new List<VariableNode>(variables.Count);
            foreach (var variable in variables)
            {
                var node = new VariableNode(variable.Name, variable.Value, variable.Type, variable.VariablesReference, _source, reference, variable.EvaluateName) { Icon = "symbol-local", Counts = ChildCounts.Of(variable) };
                _source.Track(node);
                roots.Add(node);
            }

            Replace(roots);
            IsEmpty = Roots.Count == 0;
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            if (generation == _generation)
            {
                Replace([new VariableNode(string.Empty, ex.Message, null, 0, null) { IsError = true, Icon = "error" }]);
                IsEmpty = false;
            }
        }
    }

    /// <summary>Identifiers on the frame's line and the line before it (Autos).</summary>
    private HashSet<string> AutosNames(StackFrame frame)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (frame.Source?.Path is not { } path)
        {
            return names;
        }

        foreach (var line in new[] { frame.Line - 1, frame.Line })
        {
            if (_lineText(path, line) is not { } text)
            {
                continue;
            }

            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var identifier = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
                if (identifier && start < 0)
                {
                    start = i;
                }
                else if (!identifier && start >= 0)
                {
                    if (!char.IsDigit(text[start]))
                    {
                        names.Add(text[start..i]);
                    }

                    start = -1;
                }
            }
        }

        return names;
    }

    /// <summary>Sets a variable's value: <c>setVariable</c> in its container (Locals, Autos and their members).</summary>
    public Task<string?> SetValueAsync(VariableNode node, string value) => VariableEditing.SetAsync(_debugger, node, value, isWatchRoot: false);

    /// <summary>Why <paramref name="node"/>'s value can't be changed in place (its tooltip); null when it can.</summary>
    public string? ReadOnlyReason(VariableNode node) => VariableEditing.ReadOnlyReason(_debugger, node);
}

/// <summary>
/// The Watch window: user expressions, evaluated in the selected frame on every stop while the window is shown, and once
/// when it's shown (M56).
/// </summary>
public sealed class WatchViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.watch";

    private readonly DebugService _debugger;
    private readonly VariableSource _source;
    private readonly ShownRefresh _refresh;
    private string _newExpression = string.Empty;
    private int _generation;

    public WatchViewModel(DebugService debugger)
    {
        _debugger = debugger;
        _source = new VariableSource(debugger);
        _refresh = new ShownRefresh(() => _ = RefreshAsync());
        debugger.Stopped += (_, _) => Ui.Post(_refresh.Request);
        debugger.FrameChanged += (_, _) => Ui.Post(_refresh.Request);
        debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (debugger.State == DebugState.Idle)
            {
                foreach (var node in Roots)
                {
                    node.Update(string.Empty, null, 0);
                }
            }
        });
    }

    public ObservableCollection<VariableNode> Roots { get; } = [];

    public IReadOnlyList<string> Expressions => [.. Roots.Select(r => r.Name)];

    void IShownAware.SetViewShown(bool shown) => _refresh.SetShown(shown);

    /// <summary>What's expanded, by evaluate name: opened again when a watch's value changes (D44, decision 9).</summary>
    public ExpansionMemory Expansion { get; } = new();

    public string NewExpression
    {
        get => _newExpression;
        set => SetField(ref _newExpression, value ?? string.Empty);
    }

    public event EventHandler? Changed;

    public void Load(IEnumerable<string>? expressions)
    {
        Roots.Clear();
        foreach (var expression in expressions ?? [])
        {
            Roots.Add(Node(expression));
        }
    }

    public async Task AddAsync(string expression)
    {
        expression = expression.Trim();
        if (expression.Length == 0)
        {
            return;
        }

        var node = Node(expression);
        Roots.Add(node);
        NewExpression = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
        await EvaluateAsync(node, null).ConfigureAwait(true); // its first value, whatever refresh runs meanwhile
    }

    public void Remove(VariableNode node)
    {
        Roots.Remove(node);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private VariableNode Node(string expression) => new(expression, string.Empty, null, 0, _source, evaluateName: expression) { Icon = IconOf(expression) };

    /// <summary>A watch's icon: a local for a name, else a member's guessed from the expression's last name.</summary>
    internal static string IconOf(string expression)
    {
        var dot = expression.LastIndexOf('.');
        return dot < 0 || dot == expression.Length - 1 ? "symbol-local" : VariableIcons.Guess(expression[(dot + 1)..]);
    }

    /// <summary>Why <paramref name="node"/>'s value can't be changed in place (its tooltip); null when it can.</summary>
    public string? ReadOnlyReason(VariableNode node) => VariableEditing.ReadOnlyReason(_debugger, node);

    /// <summary>Sets a watch's value (<c>setExpression</c>) or a member's (<c>setVariable</c>), then re-evaluates the watches.</summary>
    public async Task<string?> SetValueAsync(VariableNode node, string value)
    {
        var error = await VariableEditing.SetAsync(_debugger, node, value, Roots.Contains(node)).ConfigureAwait(true);
        if (error is null)
        {
            await RefreshAsync().ConfigureAwait(true);
        }

        return error;
    }

    public async Task RefreshAsync()
    {
        _refresh.Fresh();
        var generation = ++_generation;
        foreach (var node in Roots.ToList())
        {
            if (generation != _generation)
            {
                return; // the next stop (or frame) evaluates them again
            }

            await EvaluateAsync(node, generation).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Evaluates a watch. An answer for an earlier stop or frame is dropped (M56): it could come after the next refresh's
    /// and overwrite the newer value.
    /// </summary>
    private async Task EvaluateAsync(VariableNode node, int? generation)
    {
        if (_debugger.State != DebugState.Stopped)
        {
            return;
        }

        try
        {
            var result = await _debugger.EvaluateAsync(node.Name, "watch").ConfigureAwait(true);
            if (generation is { } asked && asked != _generation)
            {
                return;
            }

            node.SetError(false, canReevaluate: false);
            node.Update(result.Result, result.Type, result.VariablesReference, ChildCounts.Of(result));
            _source.Track(node);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            if (generation is { } asked && asked != _generation)
            {
                return;
            }

            // An expression that doesn't evaluate: the error icon and color (C2).
            node.SetError(true, canReevaluate: false);
            node.Update(ex.Message, null, 0);
            node.IsChanged = false;
        }
    }
}

/// <summary>A frame in the Call Stack window.</summary>
public sealed record FrameRow(StackFrame Frame, bool IsSelected, bool IsTop, bool IsExternalCode = false)
{
    public const string ExternalCode = "[External Code]";

    /// <summary>
    /// The row a stopped thread without frames shows (a program paused before its Main runs, or in native code only),
    /// as the classic IDE does: not a frame of the adapter's, so it selects nothing.
    /// </summary>
    public static FrameRow ExternalCodeRow() => new(new StackFrame(0, ExternalCode, null, 0, 0), IsSelected: false, IsTop: true, IsExternalCode: true);

    public string Name => Frame.Name;

    /// <summary>The file and line; nothing for a label (its name says what it is) or the external-code row, "[External Code]" without source.</summary>
    public string Location => IsExternalCode ? string.Empty : Frame.Source?.Path is { } path ? $"{System.IO.Path.GetFileName(path)}, line {Frame.Line}" : IsLabel ? string.Empty : ExternalCode;

    /// <summary>The location's tooltip: the source's full path and line (null without source: no tooltip).</summary>
    public string? LocationTip => Frame.Source?.Path is { } path ? $"{path}, line {Frame.Line}" : Location.Length > 0 ? Location : null;

    public bool HasSource => Frame.Source?.Path is not null;

    /// <summary>
    /// A frame that is only a label (DAP's <c>label</c> hint: the desktop-CLR debugger's internal frames such as
    /// "[Managed to Native Transition]", M43b), shown in italics without a location.
    /// </summary>
    public bool IsLabel => Frame.PresentationHint == "label";

    /// <summary>Shown dimmed: no source, a label, or the adapter's <c>subtle</c> hint (code outside Just My Code, M43b).</summary>
    public bool IsDimmed => !HasSource || IsLabel || Frame.PresentationHint == "subtle";

    public string Marker => IsTop ? "▶" : IsSelected ? "▷" : string.Empty;

    public string AccessibleName => IsExternalCode ? Name : $"{Name}{(Location.Length > 0 ? ", " + Location : string.Empty)}{(IsSelected ? ", selected" : string.Empty)}";
}

/// <summary>The Call Stack window: the threads and the selected thread's frames.</summary>
public sealed class CallStackViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.callStack";

    private readonly DebugService _debugger;
    private readonly Func<string, int, Task> _open;
    private DapThread? _selectedThread;
    private bool _syncing;

    public CallStackViewModel(DebugService debugger, Func<string, int, Task> open)
    {
        _debugger = debugger;
        _open = open;
        debugger.Stopped += (_, _) => Ui.Post(Refresh);
        debugger.FrameChanged += (_, _) => Ui.Post(Refresh);
        debugger.StateChanged += (_, _) => Ui.Post(Refresh);
    }

    public ObservableCollection<DapThread> Threads { get; } = [];

    public ObservableCollection<FrameRow> Frames { get; } = [];

    public DapThread? SelectedThread
    {
        get => _selectedThread;
        set
        {
            if (SetField(ref _selectedThread, value) && !_syncing && value is not null && value.Id != _debugger.SelectedThreadId)
            {
                _ = _debugger.SelectThreadAsync(value.Id);
            }
        }
    }

    public bool IsEmpty => Frames.Count == 0;

    public void Refresh()
    {
        _syncing = true;
        try
        {
            Threads.Clear();
            Frames.Clear();
            if (_debugger.State == DebugState.Stopped)
            {
                foreach (var thread in _debugger.Threads)
                {
                    Threads.Add(thread);
                }

                SelectedThread = Threads.FirstOrDefault(t => t.Id == _debugger.SelectedThreadId);
                var frames = _debugger.Frames;
                for (var i = 0; i < frames.Count; i++)
                {
                    Frames.Add(new FrameRow(frames[i], frames[i] == _debugger.SelectedFrame, i == 0));
                }

                if (frames.Count == 0)
                {
                    Frames.Add(FrameRow.ExternalCodeRow());
                }
            }
            else
            {
                SelectedThread = null;
            }
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Selects a frame (Locals, Autos and Watch follow) and opens its source.</summary>
    public async Task ActivateAsync(FrameRow row)
    {
        if (row.IsExternalCode)
        {
            return;
        }

        _debugger.SelectFrame(row.Frame);
        if (row.Frame.Source?.Path is not { } path)
        {
            return;
        }

        try
        {
            // Through the file system that holds it: a remote window's sources are the server's (M21).
            if (!await FileSystems.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(true))
            {
                return;
            }
        }
        catch (IOException)
        {
            return;
        }

        await _open(path, row.Frame.Line).ConfigureAwait(true);
    }
}

/// <summary>The Debug Console: program and debugger output, and an input line that evaluates expressions.</summary>
public sealed class DebugConsoleViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.console";

    private readonly DebugService _debugger;
    private readonly List<string> _history = [];
    private string _input = string.Empty;
    private int _historyIndex;

    public DebugConsoleViewModel(DebugService debugger, OutputChannel channel, EditorFont font)
    {
        _debugger = debugger;
        Channel = channel;
        Font = font;
    }

    public OutputChannel Channel { get; }

    public EditorFont Font { get; }

    public string Input
    {
        get => _input;
        set => SetField(ref _input, value ?? string.Empty);
    }

    /// <summary>Evaluates <see cref="Input"/> in the selected frame and writes the result.</summary>
    public async Task SubmitAsync()
    {
        var expression = Input.Trim();
        if (expression.Length == 0)
        {
            return;
        }

        _history.Remove(expression);
        _history.Add(expression);
        _historyIndex = _history.Count;
        Input = string.Empty;
        Channel.AppendLine("> " + expression);
        try
        {
            var result = await _debugger.EvaluateAsync(expression, "repl").ConfigureAwait(true);
            Channel.AppendLine(string.IsNullOrEmpty(result.Type) ? result.Result : $"{result.Result}   ({result.Type})");
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
        {
            Channel.AppendLine("error: " + ex.Message);
        }
    }

    /// <summary>Up and Down walk the input history.</summary>
    public void History(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        Input = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
    }

    public void Clear() => Channel.Clear();
}

/// <summary>A row of the Breakpoints window: a source breakpoint (<see cref="Spec"/>) or a function breakpoint (<see cref="Function"/>).</summary>
public sealed class BreakpointRow : ObservableObject
{
    private readonly Action<BreakpointRow, bool> _setEnabled;

    public BreakpointRow(BreakpointSpec spec, BreakpointState? state, Action<BreakpointRow, bool> setEnabled)
    {
        Spec = spec;
        State = state;
        _setEnabled = setEnabled;
    }

    public BreakpointRow(FunctionBreakpointSpec function, BreakpointState? state, Action<BreakpointRow, bool> setEnabled)
    {
        Function = function;
        State = state;
        _setEnabled = setEnabled;
    }

    public BreakpointSpec? Spec { get; }

    public FunctionBreakpointSpec? Function { get; }

    public BreakpointState? State { get; }

    public string Location => Spec is { } spec ? $"{System.IO.Path.GetFileName(spec.Path)}, line {spec.Line}" : $"Function {Function!.Name}";

    public string Labels => Spec?.Labels ?? Function?.Labels ?? string.Empty;

    public bool HasLabels => Labels.Length > 0;

    public string Details => string.Join("; ", new[]
    {
        (Spec?.Condition ?? Function?.Condition) is { } c ? "when " + c : null,
        Spec?.HitCondition is { } h ? "hit count " + h : null,
        Spec?.Filter is { } f ? "filter " + f : null,
        Spec?.LogMessage is { } m ? "log \"" + m + "\"" : null,
        Spec?.Temporary == true ? "temporary" : null,
        Spec?.DependsOn is { } d ? $"after {System.IO.Path.GetFileName(d.Path)}, line {d.Line}" : null,
        State is { Verified: false, Message: { } message } ? message : null,
    }.OfType<string>());

    public bool IsVerified => State?.Verified ?? true;

    public bool Enabled
    {
        get => Spec?.Enabled ?? Function!.Enabled;
        set
        {
            if (value != Enabled)
            {
                _setEnabled(this, value);
            }
        }
    }

    /// <summary>Whether the row matches the window's search text (location, condition, labels).</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Location.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Labels.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Details.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || (Spec?.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    public string AccessibleName => $"Breakpoint {Location}{(Enabled ? string.Empty : ", disabled")}{(HasLabels ? ", labels " + Labels : string.Empty)}{(Details.Length > 0 ? ", " + Details : string.Empty)}";
}

/// <summary>
/// The Breakpoints window: source and function breakpoints, searchable by location, condition or label. The rows are
/// built again once per burst of changes, with one reset, and only while the window is shown (M56): a session's start
/// sent one setBreakpoints per file, and each answer rebuilt every row.
/// </summary>
public sealed class BreakpointsViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.breakpoints";

    private readonly DebugService _debugger;
    private readonly Func<string, int, Task> _open;
    private readonly ShownRefresh _refresh;
    private bool _refreshQueued;
    private string _filter = string.Empty;

    public BreakpointsViewModel(DebugService debugger, Func<string, int, Task> open)
    {
        _debugger = debugger;
        _open = open;
        _refresh = new ShownRefresh(Refresh);
        debugger.Breakpoints.Changed += (_, _) => Ui.Post(QueueRefresh);
        debugger.Breakpoints.FunctionsChanged += (_, _) => Ui.Post(QueueRefresh);
        debugger.Breakpoints.StatesChanged += (_, _) => Ui.Post(QueueRefresh);
    }

    public ResettableCollection<BreakpointRow> Rows { get; } = [];

    void IShownAware.SetViewShown(bool shown) => _refresh.SetShown(shown);

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Search text: matches locations, conditions and labels.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            _refresh.Request();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Refresh()
    {
        _refresh.Fresh();
        var store = _debugger.Breakpoints;
        var filter = _filter.Trim();
        Rows.Reset([.. store.All.Select(b => new BreakpointRow(b, store.StateOf(b), SetEnabled))
            .Concat(store.Functions.Select(f => new BreakpointRow(f, store.StateOf(f), SetEnabled)))
            .Where(r => r.Matches(filter))]);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SetEnabled(BreakpointRow row, bool enabled)
    {
        if (row.Spec is { } spec)
        {
            _debugger.Breakpoints.Set(spec with { Enabled = enabled });
        }
        else if (row.Function is { } function)
        {
            _debugger.Breakpoints.SetFunction(function with { Enabled = enabled });
        }
    }

    /// <summary>Enables or disables every breakpoint shown (the search applies).</summary>
    public void SetAllEnabled(bool enabled)
    {
        foreach (var row in Rows.ToList())
        {
            if (row.Enabled != enabled)
            {
                SetEnabled(row, enabled);
            }
        }
    }

    public Task OpenAsync(BreakpointRow row) => row.Spec is { } spec ? _open(spec.Path, spec.Line) : Task.CompletedTask;

    public void Remove(BreakpointRow row)
    {
        if (row.Spec is { } spec)
        {
            _debugger.Breakpoints.Remove(spec);
        }
        else if (row.Function is { } function)
        {
            _debugger.Breakpoints.RemoveFunction(function);
        }
    }

    /// <summary>Sets a row's labels (a comma-separated list).</summary>
    public void SetLabels(BreakpointRow row, string? labels)
    {
        var normalized = BreakpointStore.JoinLabels(BreakpointStore.SplitLabels(labels));
        if (row.Spec is { } spec)
        {
            _debugger.Breakpoints.Set(spec with { Labels = normalized });
        }
        else if (row.Function is { } function)
        {
            _debugger.Breakpoints.SetFunction(function with { Labels = normalized });
        }
    }
}

/// <summary>Editing values in the debugger's trees (ADR 0021) and in the data tip, in place (D44, decision 6).</summary>
internal static class VariableEditing
{
    /// <summary>
    /// Why a value can't be changed (a property without a setter, a constant, a read-only field, an engine that changes
    /// nothing), said in its tooltip; empty for a row with nothing to change (a group, a placeholder, an error); null when
    /// it can be changed, in place.
    /// </summary>
    public static string? ReadOnlyReason(DebugService debugger, VariableNode node)
    {
        if (node.IsGroup || node.IsPlaceholder || node.IsError || debugger.State != DebugState.Stopped)
        {
            return string.Empty;
        }

        if (!debugger.CanSetValues)
        {
            return Localizer.Get("dataTip.engineCantChange", "This debugger can't change values.");
        }

        if (node.CanWrite == false)
        {
            return node.Kind switch
            {
                MemberKind.Property => Localizer.Format("dataTip.noSetter", "{0} can't be changed: it has no setter.", node.Name),
                MemberKind.Constant => Localizer.Format("dataTip.constant", "{0} can't be changed: it's a constant.", node.Name),
                _ => Localizer.Format("dataTip.readOnly", "{0} can't be changed: it's read-only.", node.Name),
            };
        }

        return node.EvaluateName is null && node.ParentReference is null
            ? Localizer.Format("dataTip.cantChange", "{0} can't be changed here.", node.Name)
            : null;
    }

    /// <summary>
    /// A root of Watch or QuickWatch is assigned with <c>setExpression</c>; a variable with <c>setVariable</c> in its
    /// container, or by its evaluate name when it has none. Returns an error message, or null.
    /// </summary>
    public static async Task<string?> SetAsync(DebugService debugger, VariableNode node, string value, bool isWatchRoot)
    {
        try
        {
            if (!isWatchRoot && node.ParentReference is { } parent)
            {
                var result = await debugger.SetVariableAsync(parent, node.Name, value).ConfigureAwait(true);
                node.Update(result.Value, result.Type ?? node.Type, result.VariablesReference ?? 0, ChildCounts.Of(result.Type ?? node.Type, result.NamedVariables, result.IndexedVariables));
                return null;
            }

            if (node.EvaluateName is { } expression)
            {
                var result = await debugger.SetExpressionAsync(expression, value).ConfigureAwait(true);
                node.Update(result.Value, result.Type ?? node.Type, result.VariablesReference ?? 0, ChildCounts.Of(result.Type ?? node.Type, result.NamedVariables, result.IndexedVariables));
                return null;
            }

            return "This value can't be changed.";
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
