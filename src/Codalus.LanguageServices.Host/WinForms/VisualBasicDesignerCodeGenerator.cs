using System.Globalization;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// Writes designer statements and values as Visual Basic (ADR 0073, Visual Basic forms), in the style of the file: the
/// classic IDE for .NET (no <c>Me.</c>, types shortened through the file's and the project's imports, <c>A.X Or A.Y</c>,
/// <c>CByte(n)</c>, <c>vbCrLf</c>) or for .NET Framework (<c>Me.</c>, full type names, CodeDOM's <c>CType</c>,
/// <c>Global.Microsoft.VisualBasic.ChrW(n)</c>). Components are <c>Friend WithEvents</c> fields; an event the designer
/// hooks in InitializeComponent is an <c>AddHandler</c> (a <c>Handles</c> clause is written in the code file).
/// </summary>
internal sealed class VisualBasicDesignerCodeGenerator(DesignerModel model, string indent, string newLine) : IDesignerCodeGenerator
{
    private static readonly Dictionary<string, string> s_keywords = new(StringComparer.Ordinal)
    {
        ["System.Object"] = "Object", ["System.String"] = "String", ["System.Boolean"] = "Boolean", ["System.Char"] = "Char",
        ["System.Byte"] = "Byte", ["System.SByte"] = "SByte", ["System.Int16"] = "Short", ["System.UInt16"] = "UShort", ["System.Int32"] = "Integer",
        ["System.UInt32"] = "UInteger", ["System.Int64"] = "Long", ["System.UInt64"] = "ULong", ["System.Single"] = "Single", ["System.Double"] = "Double",
        ["System.Decimal"] = "Decimal", ["System.DateTime"] = "Date",
    };

    private readonly HashSet<string> _locals = model.Components.Where(c => c.IsLocal).Select(c => c.Name).Append("resources").ToHashSet(StringComparer.OrdinalIgnoreCase);

    private bool Qualified => model.QualifiedNames;

    private string Me => model.UsesThis ? "Me." : string.Empty;

    private string FullClassName => model.Namespace is null ? model.ClassName : model.Namespace + "." + model.ClassName;

    public string Statement(DesignerStatement statement, string? sectionRule = null)
    {
        sectionRule ??= "' ";
        var code = statement.Kind switch
        {
            DesignerStatementKinds.Section => null,
            DesignerStatementKinds.Create => Create(statement),
            DesignerStatementKinds.Assign => $"{Target(statement.Target, statement.Member)} = {Value(statement.Value!)}",
            DesignerStatementKinds.Call => $"{Target(statement.Target, statement.Member)}({Arguments(statement.Arguments)})",
            DesignerStatementKinds.Layout => Layout(statement),
            DesignerStatementKinds.Event => Event(statement),
            DesignerStatementKinds.ApplyResources => $"resources.ApplyResources({Reference(statement.Target)}, {Value(statement.Value!)})",
            _ => throw new ArgumentException($"Unknown statement kind {statement.Kind}.", nameof(statement)),
        };
        if (code is null)
        {
            var rule = indent + sectionRule + newLine;
            return rule + indent + (sectionRule == "'" ? "'" : "' ") + statement.Target + newLine + rule;
        }

        return indent + code + newLine;
    }

    /// <summary><c>Dim ListViewItem1 As ListViewItem = New ListViewItem("Hammer")</c>.</summary>
    public string LocalDeclaration(string name, DesignerValue value) => $"{indent}Dim {name} As {Type(value.Type!)} = {Value(value)}{newLine}";

    public string Section(string name, string sectionRule) => Statement(new DesignerStatement { Kind = DesignerStatementKinds.Section, Target = name }, sectionRule);

    public string Field(string name, string type) => $"Friend WithEvents {name} As {Type(type)}";

    public string StringLiteral(string text) => VisualBasicStrings.Literal(text, codeDom: Qualified);

    private string Create(DesignerStatement statement)
    {
        var value = Value(statement.Value!);
        return _locals.Contains(statement.Target)
            ? $"Dim {statement.Target} As {Type(statement.Value!.Type!)} = {value}"
            : $"{Me}{statement.Target} = {value}";
    }

    private string Layout(DesignerStatement statement) => statement.Member is "BeginInit" or "EndInit"
        ? $"CType({Reference(statement.Target)}, {Type("System.ComponentModel.ISupportInitialize")}).{statement.Member}()"
        : $"{Target(statement.Target, statement.Member)}({Arguments(statement.Arguments)})";

    /// <summary><c>AddHandler Me.Button1.Click, AddressOf Me.Button1_Click</c>.</summary>
    private string Event(DesignerStatement statement)
    {
        var handler = $"AddressOf {Me}{statement.Handler}";
        var target = Target(statement.Target, statement.Member);
        return statement.Value is { Kind: DesignerValueKinds.New, Type: { } delegateType }
            ? $"AddHandler {target}, New {Type(delegateType)}({handler})"
            : $"AddHandler {target}, {handler}";
    }

    /// <summary>What a statement acts on: <c>Button1.Text</c>, <c>Me.Button1.Text</c>, <c>Text</c> (the root), <c>ListViewItem1.Text</c> (a local).</summary>
    public string Target(string target, string? member)
    {
        var prefix = target.Length == 0 ? Me : _locals.Contains(target) ? target + "." : Me + target + ".";
        if (member is null)
        {
            return target.Length == 0 ? "Me" : prefix.TrimEnd('.');
        }

        return prefix + member;
    }

    private string Reference(string name) => name.Length == 0 ? "Me" : _locals.Contains(name) ? name : Me + name;

    private string Arguments(DesignerValue[]? arguments) => arguments is null ? string.Empty : string.Join(", ", arguments.Select(Value));

    /// <summary>A type by the file's style: a keyword (<c>Integer</c>), its full name, or the shortest name the imports and the namespace allow.</summary>
    public string Type(string fullName)
    {
        fullName = fullName.Replace('+', '.');
        if (s_keywords.TryGetValue(fullName, out var keyword))
        {
            return keyword;
        }

        if (string.Equals(fullName, FullClassName, StringComparison.OrdinalIgnoreCase))
        {
            return model.ClassName;
        }

        // The project's My namespace (My.Resources.Resources, My.Settings): from the global namespace, as the classic IDE
        // writes a project resource (Global.WinFormsApp1.My.Resources.Resources.Logo).
        if (fullName.Contains(".My.", StringComparison.Ordinal) || fullName.StartsWith("My.", StringComparison.Ordinal))
        {
            return "Global." + fullName;
        }

        if (Qualified)
        {
            return fullName;
        }

        var dot = fullName.LastIndexOf('.');
        if (dot < 0)
        {
            return fullName;
        }

        var best = fullName;
        if (model.Imports.Contains(fullName[..dot], StringComparer.OrdinalIgnoreCase))
        {
            best = fullName[(dot + 1)..];
        }

        // Relative to the namespace the class is in (the project's root namespace included), or one of its parents.
        if (model.Namespace is { } ns)
        {
            var scope = ns;
            while (scope.Length > 0)
            {
                if (fullName.StartsWith(scope + ".", StringComparison.OrdinalIgnoreCase) && fullName.Length - scope.Length - 1 < best.Length)
                {
                    best = fullName[(scope.Length + 1)..];
                }

                var parent = scope.LastIndexOf('.');
                scope = parent < 0 ? string.Empty : scope[..parent];
            }
        }

        return best;
    }

    public string Value(DesignerValue value) => value.Kind switch
    {
        DesignerValueKinds.Literal => Literal(value),
        DesignerValueKinds.Enum => Enum(value),
        DesignerValueKinds.Member => $"{Type(value.Type!)}.{value.Value}",
        DesignerValueKinds.New => $"New {Type(value.Type!)}({Arguments(value.Arguments)})",
        DesignerValueKinds.Call => Call(value),
        DesignerValueKinds.Array => Array(value),
        DesignerValueKinds.Resource => Resource(value),
        DesignerValueKinds.Reference => Reference(value.Value ?? string.Empty),
        DesignerValueKinds.TypeOf => $"GetType({Type(value.Type!)})",
        _ => throw new ArgumentException($"Unknown value kind {value.Kind}.", nameof(value)),
    };

    private string Literal(DesignerValue value)
    {
        var text = value.Value ?? string.Empty;
        return value.Type switch
        {
            "null" => "Nothing",
            "string" => StringLiteral(text),
            "char" => Char(text.Length > 0 ? text[0] : '\0'),
            "bool" => text == "true" ? "True" : "False",
            "float" => Qualified ? Number(text, withPoint: true) + "!" : Number(text, withPoint: false) + "F",
            "double" => Number(text, withPoint: true) + "R",
            "decimal" => text + "D",
            "long" => text + "L",
            "uint" => text + "UI",
            "ulong" => text + "UL",
            "short" => text + "S",
            "ushort" => text + "US",
            "byte" => Qualified ? $"CType({text}, Byte)" : $"CByte({text})",
            "sbyte" => Qualified ? $"CType({text}, SByte)" : $"CSByte({text})",
            _ => text,
        };
    }

    /// <summary>A character: <c>"*"c</c>, <c>ChrW(13)</c>; CodeDOM writes each as <c>Global.Microsoft.VisualBasic.ChrW(n)</c>.</summary>
    private string Char(char c) => Qualified
        ? $"Global.Microsoft.VisualBasic.ChrW({(int)c})"
        : VisualBasicStrings.NeedsChrW(c) ? $"ChrW({(int)c})" : $"\"{(c == '"' ? "\"\"" : c.ToString())}\"c";

    /// <summary>A whole number without its fraction (<c>7</c>), or with CodeDOM's (<c>6.0</c>).</summary>
    private static string Number(string text, bool withPoint) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? number.ToString("0", CultureInfo.InvariantCulture) + (withPoint ? ".0" : string.Empty)
            : text;

    private string Enum(DesignerValue value)
    {
        var type = Type(value.Type!);
        if (value.Members is not { Length: > 0 } members)
        {
            return $"CType({value.Value}, {type})";
        }

        if (members.Length == 1)
        {
            return $"{type}.{members[0]}";
        }

        if (!Qualified)
        {
            return string.Join(" Or ", members.Select(m => $"{type}.{m}"));
        }

        // CodeDOM: CType((T.A Or T.B), T), and from three members on each further one on its own line, continued:
        // CType(((T.A Or T.B) _ ⏎ Or T.C), T).
        var combined = $"({type}.{members[0]} Or {type}.{members[1]})";
        foreach (var member in members.Skip(2))
        {
            combined = $"({combined} _{newLine}{indent}Or {type}.{member})";
        }

        return $"CType({combined}, {type})";
    }

    private string Call(DesignerValue value)
    {
        // CodeDOM wrote Color.FromArgb's components as CType(CType(n, Byte), Integer).
        if (Qualified && value is { Type: "System.Drawing.Color", Value: "FromArgb" } && value.Arguments is { Length: 3 or 4 } components
            && components.All(a => a is { Kind: DesignerValueKinds.Literal, Type: "int" or "byte" }))
        {
            return $"{Type(value.Type)}.FromArgb({string.Join(", ", components.Select(a => $"CType(CType({a.Value}, Byte), Integer)"))})";
        }

        return $"{Type(value.Type!)}.{value.Value}({Arguments(value.Arguments)})";
    }

    private string Array(DesignerValue value)
    {
        var items = value.Arguments ?? [];
        return $"New {Type(value.Type!)}() {{{string.Join(", ", items.Select(Value))}}}";
    }

    private string Resource(DesignerValue value)
    {
        var key = VisualBasicStrings.Literal(value.Value ?? string.Empty, codeDom: false);
        if (value.Type == "System.String")
        {
            return $"resources.GetString({key})";
        }

        return value.Type is { } type ? $"CType(resources.GetObject({key}), {Type(type)})" : $"resources.GetObject({key})";
    }
}

/// <summary>Whether a name can be a component's in a designer file's language.</summary>
internal static class DesignerIdentifiers
{
    public static bool IsValid(string language, string name) => language == DesignerLanguages.VisualBasic
        ? Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsValidIdentifier(name) && Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.GetKeywordKind(name) == Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.None
        : Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(name) && Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(name) == Microsoft.CodeAnalysis.CSharp.SyntaxKind.None;
}
