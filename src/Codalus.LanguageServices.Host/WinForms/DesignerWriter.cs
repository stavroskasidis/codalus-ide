using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// Applies the designer's changes to a designer file's text (M41a, ADR 0073) as the smallest edits that make the code
/// say the new thing, placed where the classic IDE's designer puts them: a property's assignment in its component's
/// section in alphabetical order (events after the properties), a new component's field after the last one, its
/// creation after the last creation, its section before the next component's (the root's last), and its
/// <c>Controls.Add</c> first in its parent's (new controls go to the front). Nothing else in the file moves, so an
/// unchanged statement keeps its text byte for byte. M41b adds the z-order (<c>Controls.Add</c>'s order), extender
/// properties, collections, locals, data bindings, and a localizable form's resources: the form's <c>.resx</c> files
/// (<paramref name="resources"/>) are edited with it, and the <c>resources</c> variable comes and goes with its uses.
/// </summary>
internal sealed class DesignerWriter(Func<SourceText, DesignerSource> read, DesignerResourceSet? resources = null)
{
    private const string EventKeyPrefix = "￿";

    /// <summary>An <c>ImageList</c>'s <c>Images.SetKeyName</c> calls: after its properties, before its events, as the classic IDE writes them.</summary>
    private const string LastKeyPrefix = "￾";

    /// <summary>Applies <paramref name="edits"/> in order and returns the new text.</summary>
    public SourceText Apply(SourceText text, IEnumerable<DesignerEdit> edits)
    {
        foreach (var edit in edits)
        {
            text = Apply(text, edit);
        }

        return text;
    }

    public SourceText Apply(SourceText text, DesignerEdit edit)
    {
        var applied = ApplyOne(text, edit);
        return edit.Kind is DesignerEditKinds.SetProperty or DesignerEditKinds.AddComponent or DesignerEditKinds.RemoveComponent or DesignerEditKinds.SetLocalized
            ? TidyResources(applied)
            : applied;
    }

    private SourceText ApplyOne(SourceText text, DesignerEdit edit)
    {
        var source = read(text);
        var changes = new List<TextChange>();
        switch (edit.Kind)
        {
            case DesignerEditKinds.SetProperty:
                SetProperty(source, changes, edit.Component, edit.Property ?? throw new ArgumentException("setProperty needs a property.", nameof(edit)), edit.Value);
                break;
            case DesignerEditKinds.AddComponent:
                return AddComponent(text, edit);
            case DesignerEditKinds.RemoveComponent:
                RemoveComponents(source, changes, edit.Component);
                break;
            case DesignerEditKinds.RenameComponent:
                Rename(source, changes, edit.Component, edit.NewName ?? throw new ArgumentException("renameComponent needs a new name.", nameof(edit)));
                break;
            case DesignerEditKinds.SetParent:
                return SetParent(text, edit);
            case DesignerEditKinds.AddEvent:
                AddEvent(source, changes, edit);
                break;
            case DesignerEditKinds.RemoveEvent:
                foreach (var statement in source.Statements.Where(s => s.Statement is { Kind: DesignerStatementKinds.Event } e && e.Target == edit.Component && e.Member == edit.Event))
                {
                    changes.Add(new TextChange(statement.Lines, string.Empty));
                }

                break;
            case DesignerEditKinds.SetChildIndex:
                SetChildIndex(source, changes, edit.Component, edit.Index ?? 0);
                break;
            case DesignerEditKinds.SetExtender:
                SetExtender(source, changes, edit);
                break;
            case DesignerEditKinds.SetCollection:
                SetCollection(source, changes, edit);
                break;
            case DesignerEditKinds.AddLocal:
                AddLocal(source, changes, edit);
                break;
            case DesignerEditKinds.SetDataBinding:
                SetDataBinding(source, changes, edit);
                break;
            case DesignerEditKinds.SetResource:
                SetResource(edit);
                return text;
            case DesignerEditKinds.SetLocalized:
                SetLocalized(source, changes, edit.Component, edit.Localized ?? true);
                break;
            default:
                throw new ArgumentException($"Unknown designer edit {edit.Kind}.", nameof(edit));
        }

        return Changed(text, source, changes);
    }

    /// <summary>Applies <paramref name="changes"/>, with the section comments of sections whose statements all go.</summary>
    private static SourceText Changed(SourceText text, DesignerSource source, List<TextChange> changes)
    {
        if (changes.Count == 0)
        {
            return text;
        }

        var removals = changes.Where(c => c.NewText!.Length == 0).Select(c => c.Span).ToList();
        bool Removed(TextSpan span) => removals.Any(r => r.Contains(span));
        for (var i = 0; i < source.Statements.Count; i++)
        {
            if (source.Statements[i].Statement.Kind != DesignerStatementKinds.Section || Removed(source.Statements[i].Lines))
            {
                continue;
            }

            var run = source.Statements.Skip(i + 1).TakeWhile(s => s.Statement.Kind != DesignerStatementKinds.Section).ToList();
            if (run.Count > 0 && run.All(s => Removed(s.Lines)))
            {
                changes.Add(new TextChange(source.Statements[i].Lines, string.Empty));
            }
        }

        return text.WithChanges(Normalize(changes));
    }

    private static List<TextChange> Normalize(List<TextChange> changes)
    {
        // Same-position insertions keep their order; overlapping removals merge.
        var ordered = changes.Select((c, i) => (c, i)).OrderBy(p => p.c.Span.Start).ThenBy(p => p.i).Select(p => p.c).ToList();
        var result = new List<TextChange>();
        foreach (var change in ordered)
        {
            if (result.Count > 0 && result[^1] is var last && change.Span.Start < last.Span.End)
            {
                if (change.NewText!.Length == 0 && last.NewText!.Length == 0)
                {
                    result[^1] = new TextChange(TextSpan.FromBounds(last.Span.Start, Math.Max(last.Span.End, change.Span.End)), string.Empty);
                }

                continue;
            }

            if (result.Count > 0 && result[^1] is var previous && previous.Span.IsEmpty && change.Span.IsEmpty && previous.Span.Start == change.Span.Start)
            {
                result[^1] = new TextChange(previous.Span, previous.NewText + change.NewText);
                continue;
            }

            result.Add(change);
        }

        return result;
    }

    private static DesignerCodeGenerator Generator(DesignerSource source) => new(source.Model, source.Indent, source.NewLine);

    // ------------------------------------------------------------------ which statements are a component's

    /// <summary>
    /// Whether <paramref name="statement"/> is in <paramref name="component"/>'s section: its assignments, calls and event
    /// hookups, its <c>ApplyResources</c>, and the calls of extender providers about it (<c>toolTip1.SetToolTip(button1, …)</c>).
    /// </summary>
    private static bool Belongs(DesignerSource source, DesignerStatement statement, string component) => statement.Kind switch
    {
        DesignerStatementKinds.Assign or DesignerStatementKinds.Event => statement.Target == component,
        DesignerStatementKinds.ApplyResources => statement.Target == component,
        DesignerStatementKinds.Call when IsExtender(statement) => statement.Arguments![0].Value == component,
        DesignerStatementKinds.Call => statement.Target == component,
        _ => false,
    } && (component.Length == 0 || source.Names.Contains(component) || component == "components");

    /// <summary><c>provider.SetToolTip(component, value)</c>: an extender property, written in the extended component's section.</summary>
    private static bool IsExtender(DesignerStatement statement) =>
        statement is { Kind: DesignerStatementKinds.Call, Member: { } member, Arguments: [{ Kind: DesignerValueKinds.Reference }, ..] } && member.StartsWith("Set", StringComparison.Ordinal)
        && !member.Contains('.', StringComparison.Ordinal) && statement.Arguments[0].Value != statement.Target;

    /// <summary>Where a statement sorts in its section: by property (the classic IDE sorts them by name), events after them.</summary>
    private static string Key(DesignerStatement statement) => statement.Kind switch
    {
        DesignerStatementKinds.ApplyResources => string.Empty,
        DesignerStatementKinds.Assign => statement.Member!,
        DesignerStatementKinds.Event => EventKeyPrefix + statement.Member,
        DesignerStatementKinds.Call when IsExtender(statement) => statement.Member!["Set".Length..],
        DesignerStatementKinds.Call when statement.Member!.EndsWith(".SetKeyName", StringComparison.Ordinal) => LastKeyPrefix + statement.Member,
        DesignerStatementKinds.Call when statement.Member!.LastIndexOf('.') is var dot and > 0 => statement.Member[..dot],
        _ => statement.Member ?? string.Empty,
    };

    private static int Compare(string a, string b) => StringComparer.OrdinalIgnoreCase.Compare(a, b);

    private static List<int> StatementsOf(DesignerSource source, string component) =>
        [.. Enumerable.Range(0, source.Statements.Count).Where(i => Belongs(source, source.Statements[i].Statement, component))];

    /// <summary>
    /// Where a statement with <paramref name="key"/> goes in <paramref name="component"/>'s section: before the first that
    /// sorts after it (and before a sub-section comment, <c>// splitContainer1.Panel1</c>, that leads it, unless the
    /// statement belongs in that sub-section), else after the last. Null when the component has no statements.
    /// </summary>
    private static int? InsertionPoint(DesignerSource source, string component, string key)
    {
        var mine = StatementsOf(source, component);
        if (mine.Count == 0)
        {
            return null;
        }

        foreach (var index in mine)
        {
            if (Compare(Key(source.Statements[index].Statement), key) > 0)
            {
                var start = source.Statements[index].Lines.Start;
                if (index > 0 && source.Statements[index - 1].Statement is { Kind: DesignerStatementKinds.Section } sub && sub.Target.Contains('.', StringComparison.Ordinal)
                    && !key.StartsWith(sub.Target[(sub.Target.IndexOf('.') + 1)..] + ".", StringComparison.Ordinal))
                {
                    start = source.Statements[index - 1].Lines.Start;
                }

                return start;
            }
        }

        return source.Statements[mine[^1]].Lines.End;
    }

    /// <summary>Where a new section for <paramref name="component"/> goes: before the next component's (in creation order), else before the root's.</summary>
    private static int NewSectionPoint(DesignerSource source, string component)
    {
        var order = source.Model.Components.Where(c => !c.IsLocal && !c.IsRoot).Select(c => c.Name).ToList();
        var position = order.IndexOf(component);
        foreach (var next in (position < 0 ? [] : order.Skip(position + 1)).Append(string.Empty))
        {
            if (SectionStart(source, next) is { } start)
            {
                return start;
            }
        }

        // No root statements either: before the closing layout calls, else at the end of the method.
        return TrailingLayoutStart(source) ?? BodyEnd(source);
    }

    private static int? SectionStart(DesignerSource source, string component)
    {
        var header = source.Statements.FirstOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Section && s.Statement.Target == (component.Length == 0 ? source.Model.ClassName : component));
        if (header is not null)
        {
            return header.Lines.Start;
        }

        var mine = StatementsOf(source, component);
        return mine.Count == 0 ? null : source.Statements[mine[0]].Lines.Start;
    }

    /// <summary>The start of the layout calls that close the method (<c>ResumeLayout</c>, <c>PerformLayout</c>, <c>EndInit</c>), or null.</summary>
    private static int? TrailingLayoutStart(DesignerSource source)
    {
        int? start = null;
        for (var i = source.Statements.Count - 1; i >= 0 && source.Statements[i].Statement.Kind == DesignerStatementKinds.Layout
             && source.Statements[i].Statement.Member is { } member && !member.EndsWith("SuspendLayout", StringComparison.Ordinal) && member != "BeginInit"; i--)
        {
            start = source.Statements[i].Lines.Start;
        }

        return start;
    }

    /// <summary>The start of the line holding InitializeComponent's closing brace (after the old style's blank line, if any).</summary>
    private static int BodyEnd(DesignerSource source)
    {
        var body = source.Method.Body!;
        if (body.Statements.LastOrDefault() is { } last)
        {
            return DesignerReader.LinesOf(source.Text, last.Span).End;
        }

        return source.Text.Lines.GetLineFromPosition(body.CloseBraceToken.SpanStart).Start;
    }

    private static int BodyStart(DesignerSource source) =>
        source.Statements.Count > 0 ? source.Statements[0].Lines.Start : source.Text.Lines.GetLineFromPosition(source.Method.Body!.OpenBraceToken.Span.End).EndIncludingLineBreak;

    // ------------------------------------------------------------------ properties

    private static void SetProperty(DesignerSource source, List<TextChange> changes, string component, string property, DesignerValue? value)
    {
        var existing = source.Statements.LastOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Assign } a && a.Target == component && a.Member == property);
        if (existing is not null)
        {
            if (value is null)
            {
                changes.Add(new TextChange(existing.Lines, string.Empty));
            }
            else if (existing.Syntax is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment })
            {
                changes.Add(new TextChange(assignment.Right.Span, Generator(source).Value(value)));
            }

            return;
        }

        if (value is not null)
        {
            Insert(source, changes, component, new DesignerStatement { Kind = DesignerStatementKinds.Assign, Target = component, Member = property, Value = value });
        }
    }

    /// <summary>Inserts a statement in its component's section, making the section when the component has none.</summary>
    private static void Insert(DesignerSource source, List<TextChange> changes, string component, DesignerStatement statement) =>
        InsertCode(source, changes, component, Key(statement), Generator(source).Statement(statement));

    /// <summary>Inserts statements' code where a statement with <paramref name="key"/> sorts in the component's section, making the section when the component has none.</summary>
    private static void InsertCode(DesignerSource source, List<TextChange> changes, string component, string key, string code)
    {
        if (InsertionPoint(source, component, key) is { } point)
        {
            changes.Add(new TextChange(new TextSpan(point, 0), code));
            return;
        }

        var name = component.Length == 0 ? source.Model.ClassName : component;
        changes.Add(new TextChange(new TextSpan(NewSectionPoint(source, component), 0), Generator(source).Section(name, source.SectionRule) + code));
    }

    // ------------------------------------------------------------------ components

    private SourceText AddComponent(SourceText text, DesignerEdit edit)
    {
        var name = edit.Component;
        var type = edit.Type ?? throw new ArgumentException("addComponent needs a type.", nameof(edit));
        var source = read(text);
        if (source.Names.Contains(name) || source.Fields.ContainsKey(name))
        {
            throw new InvalidOperationException($"{source.Model.ClassName} already has a member named {name}.");
        }

        var generator = Generator(source);
        var changes = new List<TextChange>();

        // components = new System.ComponentModel.Container(), when the new component is created in it.
        var arguments = edit.CreationArguments ?? [];
        if (arguments.Any(a => a is { Kind: DesignerValueKinds.Reference, Value: "components" })
            && !source.Statements.Any(s => s.Statement is { Kind: DesignerStatementKinds.Create, Target: "components" }))
        {
            changes.Add(new TextChange(new TextSpan(BodyStart(source), 0),
                generator.Statement(new DesignerStatement { Kind = DesignerStatementKinds.Create, Target = "components", Value = DesignerValue.NewOf("System.ComponentModel.Container") })));
        }

        // The field, after the last component's.
        var fieldIndent = Indentation(source.Text, source.Method.SpanStart);
        var field = $"{fieldIndent}private {generator.Type(type)} {name};{source.NewLine}";
        var lastField = source.Fields.Where(f => source.Names.Contains(f.Key)).Select(f => f.Value).OrderBy(f => f.SpanStart).LastOrDefault();
        if (lastField is not null)
        {
            changes.Add(new TextChange(new TextSpan(DesignerReader.LinesOf(source.Text, lastField.Span).End, 0), field));
        }
        else if (EndRegionAfter(source) is { } endRegion)
        {
            changes.Add(new TextChange(new TextSpan(endRegion, 0), source.NewLine + field));
        }
        else
        {
            changes.Add(new TextChange(new TextSpan(source.Text.Lines.GetLineFromPosition(source.Class.CloseBraceToken.SpanStart).Start, 0), source.NewLine + field));
        }

        // The creation, after the last one.
        var creation = generator.Statement(new DesignerStatement { Kind = DesignerStatementKinds.Create, Target = name, Value = DesignerValue.NewOf(type, arguments) });
        var lastCreate = source.Statements.LastOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Create);
        changes.Add(new TextChange(new TextSpan(lastCreate?.Lines.End ?? BodyStart(source), 0), creation));
        text = text.WithChanges(Normalize(changes));

        // The rest is placed relative to the code with the component declared (its name known to the reader).
        source = read(text);
        generator = Generator(source);
        changes.Clear();
        if (edit.SupportsInitialize)
        {
            AddLayoutPair(source, changes, name, "BeginInit", "EndInit", perform: false);
        }

        var statements = (edit.Properties ?? [])
            .Select(p => new DesignerStatement { Kind = DesignerStatementKinds.Assign, Target = name, Member = p.Name, Value = p.Value })
            .OrderBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (statements.Count > 0)
        {
            changes.Add(new TextChange(new TextSpan(NewSectionPoint(source, name), 0), generator.Section(name, source.SectionRule) + string.Concat(statements.Select(s => generator.Statement(s)))));
        }

        text = text.WithChanges(Normalize(changes));
        if (edit.Parent is { } parent)
        {
            var autoSize = edit.Properties?.Any(p => p.Name == "AutoSize" && p.Value is { Kind: DesignerValueKinds.Literal, Value: "true" }) == true;
            text = AddToParent(text, name, parent, edit.ParentPath, edit.Cell, edit.Index ?? 0, autoSize);
        }

        return text;
    }

    private static string Indentation(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position);
        var content = line.ToString();
        return content[..(content.Length - content.TrimStart().Length)];
    }

    /// <summary>After the <c>#endregion</c> line that follows InitializeComponent, if there is one.</summary>
    private static int? EndRegionAfter(DesignerSource source)
    {
        var next = source.Method.GetLastToken().GetNextToken();
        foreach (var trivia in next.LeadingTrivia)
        {
            if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
            {
                return source.Text.Lines.GetLineFromPosition(trivia.SpanStart).EndIncludingLineBreak;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds <c>target.Begin()</c> before the root's <c>SuspendLayout()</c> (or after the creations) and
    /// <c>target.End()</c> before the root's <c>ResumeLayout(false)</c> (or at the end), as the classic IDE orders them.
    /// </summary>
    private static void AddLayoutPair(DesignerSource source, List<TextChange> changes, string target, string begin, string end, bool perform)
    {
        var generator = Generator(source);
        var (component, path) = SplitPath(target);
        string Member(string method) => path is null ? method : path + "." + method;
        var rootSuspend = source.Statements.FirstOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Layout, Target: "", Member: "SuspendLayout" });
        var rootResume = source.Statements.FirstOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Layout, Target: "", Member: "ResumeLayout" });
        var beginAt = component.Length == 0
            ? source.Statements.LastOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Layout && s.Statement.Member is { } m && (m.EndsWith("SuspendLayout", StringComparison.Ordinal) || m == "BeginInit"))?.Lines.End
              ?? source.Statements.LastOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Create)?.Lines.End ?? BodyStart(source)
            : rootSuspend?.Lines.Start ?? source.Statements.LastOrDefault(s => s.Statement.Kind == DesignerStatementKinds.Create)?.Lines.End ?? BodyStart(source);
        var endAt = component.Length == 0 ? BodyEnd(source) : rootResume?.Lines.Start ?? TrailingLayoutStart(source) ?? BodyEnd(source);
        var isInit = begin == "BeginInit";
        DesignerValue[]? beginArguments = isInit ? null : [];
        changes.Add(new TextChange(new TextSpan(beginAt, 0), generator.Statement(new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = component, Member = Member(begin), Arguments = beginArguments })));
        var closing = generator.Statement(new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = component, Member = Member(end), Arguments = isInit ? null : [DesignerValue.Bool(false)] });
        if (perform)
        {
            closing += generator.Statement(new DesignerStatement { Kind = DesignerStatementKinds.Layout, Target = component, Member = Member("PerformLayout"), Arguments = [] });
        }

        changes.Add(new TextChange(new TextSpan(endAt, 0), closing));
    }

    private static (string Component, string? Path) SplitPath(string target)
    {
        var dot = target.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (target, null) : (target[..dot], target[(dot + 1)..]);
    }

    /// <summary>Adds <c>parent[.path].Controls.Add(name[, column, row])</c> at <paramref name="index"/> of the parent's, with the parent's layout calls when it's its first child.</summary>
    private SourceText AddToParent(SourceText text, string name, string parent, string? path, int[]? cell, int index, bool autoSize)
    {
        var source = read(text);
        var changes = new List<TextChange>();
        var member = (path is null ? string.Empty : path + ".") + "Controls.Add";
        var layoutTarget = path is null ? parent : parent + "." + path;
        var siblings = source.Statements.Where(s => s.Statement is { Kind: DesignerStatementKinds.Call } c && c.Target == parent && c.Member == member).ToList();
        if (siblings.Count == 0 && !source.Statements.Any(s => s.Statement is { Kind: DesignerStatementKinds.Layout } l && l.Target == parent && l.Member == (path is null ? "SuspendLayout" : path + ".SuspendLayout")))
        {
            AddLayoutPair(source, changes, layoutTarget, "SuspendLayout", "ResumeLayout", autoSize);
        }

        DesignerValue[] arguments = [DesignerValue.ReferenceTo(name), .. (cell ?? []).Select(DesignerValue.Int)];
        var statement = new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = parent, Member = member, Arguments = arguments };
        var code = Generator(source).Statement(statement);
        if (siblings.Count > 0)
        {
            var at = index < siblings.Count ? siblings[Math.Max(0, index)].Lines.Start : siblings[^1].Lines.End;
            changes.Add(new TextChange(new TextSpan(at, 0), code));
        }
        else if (path is not null && InsertionPoint(source, parent, path) is { } point)
        {
            // The first child of a SplitContainer's panel: its sub-section, where the panel sorts among the properties.
            changes.Add(new TextChange(new TextSpan(point, 0), Generator(source).Section(parent + "." + path, source.SectionRule) + code));
        }
        else
        {
            Insert(source, changes, parent, statement);
        }

        return text.WithChanges(Normalize(changes));
    }

    private SourceText SetParent(SourceText text, DesignerEdit edit)
    {
        var source = read(text);
        var changes = new List<TextChange>();
        foreach (var add in source.Statements.Where(s => IsAddOf(s.Statement, edit.Component)))
        {
            changes.Add(new TextChange(add.Lines, string.Empty));
        }

        text = Changed(text, source, changes);
        return AddToParent(text, edit.Component, edit.Parent ?? string.Empty, edit.ParentPath, edit.Cell, edit.Index ?? 0, autoSize: false);
    }

    private static bool IsAddOf(DesignerStatement statement, string name) =>
        statement is { Kind: DesignerStatementKinds.Call, Member: { } member, Arguments: [{ Kind: DesignerValueKinds.Reference } child, ..] }
        && member.EndsWith("Controls.Add", StringComparison.Ordinal) && child.Value == name;

    /// <summary>Removes a component, the controls it contains, and every statement about them (and their keys in the form's resources).</summary>
    private void RemoveComponents(DesignerSource source, List<TextChange> changes, string name)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal) { name };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var statement in source.Model.Statements)
            {
                if (statement is { Kind: DesignerStatementKinds.Call, Member: { } member, Arguments: [{ Kind: DesignerValueKinds.Reference, Value: { } child }, ..] }
                    && member.EndsWith("Controls.Add", StringComparison.Ordinal) && removed.Contains(statement.Target) && removed.Add(child))
                {
                    grew = true;
                }
            }
        }

        foreach (var file in resources?.All ?? [])
        {
            foreach (var component in removed)
            {
                file.RemoveComponent(component);
            }
        }

        foreach (var field in removed.Where(source.Fields.ContainsKey).Select(n => source.Fields[n]).Distinct())
        {
            if (field.Declaration.Variables.All(v => removed.Contains(v.Identifier.Text)))
            {
                changes.Add(new TextChange(DesignerReader.LinesOf(source.Text, field.Span), string.Empty));
            }
        }

        var generator = Generator(source);
        foreach (var statement in source.Statements)
        {
            var s = statement.Statement;
            if (s.Kind == DesignerStatementKinds.Section)
            {
                if (removed.Contains(s.Target.Split('.')[0]))
                {
                    changes.Add(new TextChange(statement.Lines, string.Empty));
                }

                continue;
            }

            if (removed.Contains(s.Target) || (s.Kind == DesignerStatementKinds.ApplyResources && removed.Contains(s.Target)))
            {
                changes.Add(new TextChange(statement.Lines, string.Empty));
                continue;
            }

            // A value or an argument that names a removed component: an array loses the item; anything else goes.
            if (s.Kind == DesignerStatementKinds.Call && s.Arguments is { Length: 1 } && s.Arguments[0] is { Kind: DesignerValueKinds.Array, Arguments: { } items }
                && items.Any(i => Mentions(i, removed)))
            {
                var kept = items.Where(i => !Mentions(i, removed)).ToArray();
                changes.Add(new TextChange(statement.Lines, kept.Length == 0 ? string.Empty
                    : generator.Statement(s with { Arguments = [s.Arguments[0] with { Arguments = kept }] })));
                continue;
            }

            if ((s.Value is { } value && Mentions(value, removed)) || (s.Arguments?.Any(a => Mentions(a, removed)) ?? false))
            {
                changes.Add(new TextChange(statement.Lines, string.Empty));
            }
        }
    }

    private static bool Mentions(DesignerValue value, HashSet<string> names) =>
        (value.Kind == DesignerValueKinds.Reference && names.Contains(value.Value ?? string.Empty)) || (value.Arguments?.Any(a => Mentions(a, names)) ?? false);

    /// <summary>
    /// Renames a component in the designer file: its field, its uses in statements, its section comments and its
    /// <c>Name</c>, its keys in the form's resources (every culture's) and the code's uses of them
    /// (<c>ApplyResources(…, "name")</c>, <c>GetObject("name.Image")</c>). (Its uses in the class's other files are
    /// renamed through the compilation by the caller.)
    /// </summary>
    private void Rename(DesignerSource source, List<TextChange> changes, string name, string newName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName) || SyntaxFacts.GetKeywordKind(newName) != SyntaxKind.None)
        {
            throw new InvalidOperationException($"{newName} isn't a valid name.");
        }

        if (source.Names.Contains(newName) || source.Fields.ContainsKey(newName))
        {
            throw new InvalidOperationException($"{source.Model.ClassName} already has a member named {newName}.");
        }

        if (source.Model.Statements.Any(s => s is { Kind: DesignerStatementKinds.ApplyResources } && s.Target == name) && resources?.Get(null) is null)
        {
            throw new InvalidOperationException($"{name}'s properties are in the localizable form's resources, which weren't found: it can't be renamed in the designer.");
        }

        foreach (var file in resources?.All ?? [])
        {
            file.RenameComponent(name, newName);
        }

        // The keys the code uses: resources.ApplyResources(x, "name"), resources.GetObject("name.Image").
        foreach (var invocation in source.Method.Body!.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "resources" }, Name.Identifier.Text: var method }
                && invocation.ArgumentList.Arguments.LastOrDefault()?.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
                && literal.Token.ValueText is var key
                && ((method == "ApplyResources" && key == name) || (method is "GetObject" or "GetString" && key.StartsWith(name + ".", StringComparison.Ordinal))))
            {
                changes.Add(new TextChange(literal.Span, SymbolDisplay.FormatLiteral(newName + key[name.Length..], quote: true)));
            }
        }

        if (source.Fields.TryGetValue(name, out var field) && field.Declaration.Variables.FirstOrDefault(v => v.Identifier.Text == name) is { } variable)
        {
            changes.Add(new TextChange(variable.Identifier.Span, newName));
        }

        foreach (var identifier in source.Method.Body!.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == name))
        {
            // button1, this.button1 (not Foo.button1, a member of something else).
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier && access.Expression is not ThisExpressionSyntax)
            {
                continue;
            }

            changes.Add(new TextChange(identifier.Identifier.Span, newName));
        }

        foreach (var statement in source.Statements)
        {
            var s = statement.Statement;
            if (s.Kind == DesignerStatementKinds.Section && (s.Target == name || s.Target.StartsWith(name + ".", StringComparison.Ordinal)))
            {
                var line = source.Text.Lines.GetLineFromPosition(statement.Lines.Start).LineNumber + 1;
                var middle = source.Text.Lines[line];
                var at = middle.ToString().IndexOf("// ", StringComparison.Ordinal) + 3;
                changes.Add(new TextChange(new TextSpan(middle.Start + at, name.Length), newName));
            }
            else if (s is { Kind: DesignerStatementKinds.Assign, Member: "Name", Value: { Kind: DesignerValueKinds.Literal, Type: "string" } literal }
                     && s.Target is var target && (target == name || target == newName) && literal.Value == name
                     && statement.Syntax is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment })
            {
                changes.Add(new TextChange(assignment.Right.Span, SymbolDisplay.FormatLiteral(newName, quote: true)));
            }
        }
    }

    // ------------------------------------------------------------------ events

    private static void AddEvent(DesignerSource source, List<TextChange> changes, DesignerEdit edit)
    {
        var eventName = edit.Event ?? throw new ArgumentException("addEvent needs an event.", nameof(edit));
        var handler = edit.Handler ?? throw new ArgumentException("addEvent needs a handler.", nameof(edit));
        var statement = new DesignerStatement
        {
            Kind = DesignerStatementKinds.Event,
            Target = edit.Component,
            Member = eventName,
            Handler = handler,
            Value = source.Model.QualifiedNames || source.Model.Statements.Any(s => s is { Kind: DesignerStatementKinds.Event, Value: not null })
                ? new DesignerValue { Kind = DesignerValueKinds.New, Type = edit.DelegateType ?? "System.EventHandler", Arguments = [] }
                : null,
        };
        var existing = source.Statements.FirstOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Event } e && e.Target == edit.Component && e.Member == eventName);
        if (existing is not null)
        {
            if (existing.Statement.Handler != handler)
            {
                changes.Add(new TextChange(existing.Lines, Generator(source).Statement(statement with { Value = existing.Statement.Value })));
            }

            return;
        }

        Insert(source, changes, edit.Component, statement);
    }

    // ------------------------------------------------------------------ z-order (M41b)

    /// <summary>
    /// Moves a control's <c>Controls.Add</c> to <paramref name="index"/> among its parent's: the code's order is the
    /// controls' order, front first (Bring to Front is 0, Send to Back the last), a tab control's pages in their order.
    /// </summary>
    private static void SetChildIndex(DesignerSource source, List<TextChange> changes, string component, int index)
    {
        var add = source.Statements.FirstOrDefault(s => IsAddOf(s.Statement, component))
            ?? throw new InvalidOperationException($"{component} isn't added to a parent's controls in InitializeComponent.");
        var all = source.Statements.Where(s => s.Statement.Kind == DesignerStatementKinds.Call && s.Statement.Target == add.Statement.Target && s.Statement.Member == add.Statement.Member).ToList();
        var siblings = all.Where(s => s != add).ToList();
        index = Math.Clamp(index, 0, siblings.Count);
        if (all.IndexOf(add) == index)
        {
            return;
        }

        var code = source.Text.ToString(add.Lines);
        changes.Add(new TextChange(add.Lines, string.Empty));
        changes.Add(new TextChange(new TextSpan(index < siblings.Count ? siblings[index].Lines.Start : siblings[^1].Lines.End, 0), code));
    }

    // ------------------------------------------------------------------ extender properties (M41b)

    /// <summary><c>provider.SetProperty(component, value)</c>: its value replaced in place, inserted where the property sorts in the component's section, or removed.</summary>
    private static void SetExtender(DesignerSource source, List<TextChange> changes, DesignerEdit edit)
    {
        var provider = edit.Provider ?? throw new ArgumentException("setExtender needs a provider.", nameof(edit));
        var member = "Set" + (edit.Property ?? throw new ArgumentException("setExtender needs a property.", nameof(edit)));
        var existing = source.Statements.FirstOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Call, Arguments: [{ Kind: DesignerValueKinds.Reference } extended, _] } call
            && call.Target == provider && call.Member == member && (extended.Value ?? string.Empty) == edit.Component);
        if (existing is not null)
        {
            if (edit.Value is null)
            {
                changes.Add(new TextChange(existing.Lines, string.Empty));
            }
            else if (existing.Syntax is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { ArgumentList.Arguments: [_, var value] } })
            {
                changes.Add(new TextChange(value.Span, Generator(source).Value(edit.Value)));
            }

            return;
        }

        if (edit.Value is not null)
        {
            Insert(source, changes, edit.Component, new DesignerStatement
            {
                Kind = DesignerStatementKinds.Call,
                Target = provider,
                Member = member,
                Arguments = [DesignerValue.ReferenceTo(edit.Component), edit.Value],
            });
        }
    }

    // ------------------------------------------------------------------ collections (M41b)

    /// <summary>
    /// A collection's statements written again: <c>AddRange(new T[] { … })</c>, one <c>Add</c> per item, or one
    /// <c>SetKeyName</c> per image, where the collection's statements were (else where the property sorts).
    /// </summary>
    private static void SetCollection(DesignerSource source, List<TextChange> changes, DesignerEdit edit)
    {
        var property = edit.Property ?? throw new ArgumentException("setCollection needs a property.", nameof(edit));
        var method = edit.Method ?? "AddRange";
        var items = edit.Items ?? [];
        string[] members = [property + ".Add", property + ".AddRange", property + ".SetKeyName"];
        var existing = source.Statements.Where(s => s.Statement is { Kind: DesignerStatementKinds.Call } call && call.Target == edit.Component && members.Contains(call.Member)).ToList();
        DesignerStatement Call(string name, params DesignerValue[] arguments) =>
            new() { Kind = DesignerStatementKinds.Call, Target = edit.Component, Member = property + "." + name, Arguments = arguments };
        List<DesignerStatement> statements = method switch
        {
            "AddRange" when items.Length == 0 => [],
            "AddRange" => [Call("AddRange", DesignerValue.ArrayOf(edit.Type ?? "System.Object", items))],
            "Add" => [.. items.Select(i => Call("Add", i))],
            "SetKeyName" => [.. items.Select((i, n) => Call("SetKeyName", DesignerValue.Int(n), i))],
            _ => throw new ArgumentException($"Unknown collection method {method}.", nameof(edit)),
        };
        var generator = Generator(source);
        var code = string.Concat(statements.Select(s => generator.Statement(s)));
        if (existing.Count > 0)
        {
            changes.Add(new TextChange(existing[0].Lines, code));
            foreach (var other in existing.Skip(1))
            {
                changes.Add(new TextChange(other.Lines, string.Empty));
            }

            return;
        }

        if (statements.Count > 0)
        {
            InsertCode(source, changes, edit.Component, Key(statements[0]), code);

            // A tool strip's first items: its layout calls, as the classic IDE writes them (a context menu's without PerformLayout).
            var type = source.Model.Components.FirstOrDefault(c => c.Name == edit.Component)?.Type ?? string.Empty;
            if (property == "Items" && (type.EndsWith("Strip", StringComparison.Ordinal) || type.EndsWith(".BindingNavigator", StringComparison.Ordinal))
                && !source.Statements.Any(s => s.Statement is { Kind: DesignerStatementKinds.Layout, Member: "SuspendLayout" } layout && layout.Target == edit.Component))
            {
                AddLayoutPair(source, changes, edit.Component, "SuspendLayout", "ResumeLayout", perform: !type.EndsWith("ContextMenuStrip", StringComparison.Ordinal));
            }
        }
    }

    /// <summary><c>Type name = value;</c> with the locals before the components are created (after <c>components</c> and <c>resources</c>), as the classic IDE declares a <c>ListViewItem</c>.</summary>
    private static void AddLocal(DesignerSource source, List<TextChange> changes, DesignerEdit edit)
    {
        var value = edit.Value ?? throw new ArgumentException("addLocal needs a value.", nameof(edit));
        if (source.Names.Contains(edit.Component) || source.Fields.ContainsKey(edit.Component))
        {
            throw new InvalidOperationException($"{source.Model.ClassName} already has a member named {edit.Component}.");
        }

        var locals = source.Model.Components.Where(c => c.IsLocal).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var at = BodyStart(source);
        foreach (var statement in source.Statements)
        {
            if (statement.Statement.Kind != DesignerStatementKinds.Create || !(statement.Statement.Target == "components" || locals.Contains(statement.Statement.Target)))
            {
                break;
            }

            at = statement.Lines.End;
        }

        changes.Add(new TextChange(new TextSpan(at, 0), Generator(source).LocalDeclaration(edit.Component, value)));
    }

    // ------------------------------------------------------------------ data bindings (M41b)

    /// <summary><c>component.DataBindings.Add(new Binding("Text", …))</c>: the property's binding replaced, added where <c>DataBindings</c> sorts, or removed.</summary>
    private static void SetDataBinding(DesignerSource source, List<TextChange> changes, DesignerEdit edit)
    {
        var property = edit.Property ?? throw new ArgumentException("setDataBinding needs a property.", nameof(edit));
        var existing = source.Statements.FirstOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Call, Member: "DataBindings.Add", Arguments: [{ Kind: DesignerValueKinds.New, Arguments: [{ Kind: DesignerValueKinds.Literal } bound, ..] }] } call
            && call.Target == edit.Component && bound.Value == property);
        var statement = edit.Value is null ? null : new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = edit.Component, Member = "DataBindings.Add", Arguments = [edit.Value] };
        if (existing is not null)
        {
            changes.Add(new TextChange(existing.Lines, statement is null ? string.Empty : Generator(source).Statement(statement)));
            return;
        }

        if (statement is not null)
        {
            Insert(source, changes, edit.Component, statement);
        }
    }

    // ------------------------------------------------------------------ resources (M41b)

    /// <summary>Sets or removes a key of the form's <c>.resx</c> for a culture (created when the form has none for it).</summary>
    private void SetResource(DesignerEdit edit)
    {
        var set = resources ?? throw new InvalidOperationException("The form's resources weren't found.");
        var key = edit.Key ?? $"{(edit.Component.Length == 0 ? "$this" : edit.Component)}.{edit.Property ?? throw new ArgumentException("setResource needs a key or a property.", nameof(edit))}";
        var file = edit.Resource is null ? set.Get(edit.Culture) : set.GetOrCreate(edit.Culture);
        file?.Set(key, edit.Resource, edit.Metadata);
    }

    /// <summary><c>resources.ApplyResources(component, "name")</c>, the first statement of the component's section, added or removed.</summary>
    private static void SetLocalized(DesignerSource source, List<TextChange> changes, string component, bool localized)
    {
        var existing = source.Statements.FirstOrDefault(s => s.Statement.Kind == DesignerStatementKinds.ApplyResources && s.Statement.Target == component);
        if (!localized)
        {
            if (existing is not null)
            {
                changes.Add(new TextChange(existing.Lines, string.Empty));
            }

            return;
        }

        if (existing is null)
        {
            Insert(source, changes, component, new DesignerStatement
            {
                Kind = DesignerStatementKinds.ApplyResources,
                Target = component,
                Value = DesignerValue.String(component.Length == 0 ? "$this" : component),
            });
        }
    }

    /// <summary>
    /// The <c>resources</c> variable (<c>ComponentResourceManager resources = new ComponentResourceManager(typeof(Form1))</c>)
    /// exists while a statement uses it: created after <c>components</c> (else first), removed with its last use.
    /// </summary>
    private SourceText TidyResources(SourceText text)
    {
        var source = read(text);
        var creation = source.Statements.FirstOrDefault(s => s.Statement is { Kind: DesignerStatementKinds.Create, Target: "resources" });
        var used = source.Statements.Any(s => s.Statement.Kind == DesignerStatementKinds.ApplyResources
            || (s.Statement.Value is { } value && UsesResource(value)) || (s.Statement.Arguments?.Any(UsesResource) ?? false));
        if (used == creation is not null)
        {
            return text;
        }

        if (creation is not null)
        {
            return text.WithChanges(new TextChange(creation.Lines, string.Empty));
        }

        var first = source.Statements.Count > 0 ? source.Statements[0] : null;
        var at = first?.Statement is { Kind: DesignerStatementKinds.Create, Target: "components" } ? first.Lines.End : BodyStart(source);
        var className = source.Model.Namespace is { } ns ? ns + "." + source.Model.ClassName : source.Model.ClassName;
        var statement = Generator(source).LocalDeclaration("resources",
            DesignerValue.NewOf("System.ComponentModel.ComponentResourceManager", new DesignerValue { Kind = DesignerValueKinds.TypeOf, Type = className }));
        return text.WithChanges(new TextChange(new TextSpan(at, 0), statement));
    }

    private static bool UsesResource(DesignerValue value) => value.Kind == DesignerValueKinds.Resource || (value.Arguments?.Any(UsesResource) ?? false);
}
