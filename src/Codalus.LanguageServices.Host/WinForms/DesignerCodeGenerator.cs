using System.Globalization;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// Writes designer statements and values as C# (M41a, ADR 0073), in the style of the file: Visual Studio for .NET
/// (no <c>this.</c>, types shortened through the file's usings and namespace, <c>A.X | A.Y</c>, method groups) or for
/// .NET Framework (<c>this.</c>, full type names, CodeDOM's parenthesized casts, <c>new System.EventHandler(this.M)</c>).
/// </summary>
internal sealed class DesignerCodeGenerator(DesignerModel model, string indent, string newLine)
{
    private static readonly Dictionary<string, string> s_keywords = new(StringComparer.Ordinal)
    {
        ["System.Object"] = "object", ["System.String"] = "string", ["System.Boolean"] = "bool", ["System.Char"] = "char",
        ["System.Byte"] = "byte", ["System.Int16"] = "short", ["System.Int32"] = "int", ["System.Int64"] = "long",
        ["System.Single"] = "float", ["System.Double"] = "double", ["System.Decimal"] = "decimal",
    };

    private readonly HashSet<string> _locals = model.Components.Where(c => c.IsLocal).Select(c => c.Name).Append("resources").ToHashSet(StringComparer.Ordinal);

    private bool Qualified => model.QualifiedNames;

    private string This => model.UsesThis ? "this." : string.Empty;

    private string FullClassName => model.Namespace is null ? model.ClassName : model.Namespace + "." + model.ClassName;

    /// <summary>A statement's lines (with its indentation and line break; three lines for a section comment).</summary>
    public string Statement(DesignerStatement statement, string sectionRule = "// ")
    {
        var code = statement.Kind switch
        {
            DesignerStatementKinds.Section => null,
            DesignerStatementKinds.Create => Create(statement),
            DesignerStatementKinds.Assign => $"{Target(statement.Target, statement.Member)} = {Value(statement.Value!)};",
            DesignerStatementKinds.Call => $"{Target(statement.Target, statement.Member)}({Arguments(statement.Arguments)});",
            DesignerStatementKinds.Layout => Layout(statement),
            DesignerStatementKinds.Event => Event(statement),
            DesignerStatementKinds.ApplyResources => $"resources.ApplyResources({Reference(statement.Target)}, {Value(statement.Value!)});",
            _ => throw new ArgumentException($"Unknown statement kind {statement.Kind}.", nameof(statement)),
        };
        if (code is null)
        {
            var rule = indent + sectionRule + newLine;
            return rule + indent + "// " + statement.Target + newLine + rule;
        }

        return indent + code + newLine;
    }

    /// <summary>A local's declaration (M41b): <c>ListViewItem listViewItem1 = new ListViewItem("Hammer");</c>.</summary>
    public string LocalDeclaration(string name, DesignerValue value) => $"{indent}{Type(value.Type!)} {name} = {Value(value)};{newLine}";

    /// <summary>A section comment for <paramref name="name"/>.</summary>
    public string Section(string name, string sectionRule) => Statement(new DesignerStatement { Kind = DesignerStatementKinds.Section, Target = name }, sectionRule);

    private string Create(DesignerStatement statement)
    {
        var value = Value(statement.Value!);
        if (_locals.Contains(statement.Target))
        {
            return $"{Type(statement.Value!.Type!)} {statement.Target} = {value};";
        }

        return $"{This}{statement.Target} = {value};";
    }

    private string Layout(DesignerStatement statement)
    {
        if (statement.Member is "BeginInit" or "EndInit")
        {
            var type = Type("System.ComponentModel.ISupportInitialize");
            return Qualified || model.UsesThis
                ? $"(({type})({Reference(statement.Target)})).{statement.Member}();"
                : $"(({type}){Reference(statement.Target)}).{statement.Member}();";
        }

        return $"{Target(statement.Target, statement.Member)}({Arguments(statement.Arguments)});";
    }

    private string Event(DesignerStatement statement)
    {
        var handler = This + statement.Handler;
        var target = Target(statement.Target, statement.Member);
        return statement.Value is { Kind: DesignerValueKinds.New, Type: { } delegateType }
            ? $"{target} += new {Type(delegateType)}({handler});"
            : $"{target} += {handler};";
    }

    /// <summary>What a statement acts on: <c>button1.Text</c>, <c>this.button1.Text</c>, <c>Text</c> (the root), <c>listViewItem1.Text</c> (a local).</summary>
    public string Target(string target, string? member)
    {
        var prefix = target.Length == 0 ? This : _locals.Contains(target) ? target + "." : This + target + ".";
        if (member is null)
        {
            return target.Length == 0 ? "this" : prefix.TrimEnd('.');
        }

        return prefix + member;
    }

    private string Reference(string name) => name.Length == 0 ? "this" : _locals.Contains(name) ? name : This + name;

    private string Arguments(DesignerValue[]? arguments) => arguments is null ? string.Empty : string.Join(", ", arguments.Select(Value));

    /// <summary>A type by the file's style: its full name, or the shortest name the file's usings and namespace allow.</summary>
    public string Type(string fullName)
    {
        fullName = fullName.Replace('+', '.');
        if (s_keywords.TryGetValue(fullName, out var keyword))
        {
            return keyword;
        }

        if (fullName == FullClassName)
        {
            return model.ClassName;
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
        if (model.Imports.Contains(fullName[..dot], StringComparer.Ordinal))
        {
            best = fullName[(dot + 1)..];
        }

        // Relative to the namespace the class is in, or one of its parents.
        if (model.Namespace is { } ns)
        {
            var scope = ns;
            while (scope.Length > 0)
            {
                if (fullName.StartsWith(scope + ".", StringComparison.Ordinal) && fullName.Length - scope.Length - 1 < best.Length)
                {
                    best = fullName[(scope.Length + 1)..];
                }

                var parent = scope.LastIndexOf('.');
                scope = parent < 0 ? string.Empty : scope[..parent];
            }
        }

        return best;
    }

    /// <summary>A value, as the file's style writes it.</summary>
    public string Value(DesignerValue value) => value.Kind switch
    {
        DesignerValueKinds.Literal => Literal(value),
        DesignerValueKinds.Enum => Enum(value),
        DesignerValueKinds.Member => $"{Type(value.Type!)}.{value.Value}",
        DesignerValueKinds.New => $"new {Type(value.Type!)}({Arguments(value.Arguments)})",
        DesignerValueKinds.Call => Call(value),
        DesignerValueKinds.Array => Array(value),
        DesignerValueKinds.Resource => Resource(value),
        DesignerValueKinds.Reference => Reference(value.Value ?? string.Empty),
        DesignerValueKinds.TypeOf => $"typeof({Type(value.Type!)})",
        _ => throw new ArgumentException($"Unknown value kind {value.Kind}.", nameof(value)),
    };

    private string Literal(DesignerValue value)
    {
        var text = value.Value ?? string.Empty;
        return value.Type switch
        {
            "null" => "null",
            "string" => SymbolDisplay.FormatLiteral(text, quote: true),
            "char" => SymbolDisplay.FormatLiteral(text.Length > 0 ? text[0] : '\0', quote: true),
            "bool" => text,
            "float" => Number(text) + "F",
            "double" => Number(text) + "D",
            "decimal" => text + "M",
            "long" => text + "L",
            "uint" => text + "U",
            "ulong" => text + "UL",
            "byte" or "short" or "sbyte" or "ushort" when Qualified => $"(({value.Type})({text}))",
            _ => text,
        };
    }

    private static string Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? number.ToString("0", CultureInfo.InvariantCulture)
            : text;

    private string Enum(DesignerValue value)
    {
        var type = Type(value.Type!);
        if (value.Members is not { Length: > 0 } members)
        {
            return Qualified ? $"(({type})({value.Value}))" : $"({type}){value.Value}";
        }

        if (members.Length == 1)
        {
            return $"{type}.{members[0]}";
        }

        if (!Qualified)
        {
            return string.Join(" | ", members.Select(m => $"{type}.{m}"));
        }

        // CodeDOM: ((T)((T.A | T.B))), and from three members on each further one on its own line at the statement's
        // indentation: ((T)(((T.A | T.B) ⏎ | T.C))).
        var combined = $"({type}.{members[0]} | {type}.{members[1]})";
        foreach (var member in members.Skip(2))
        {
            combined = $"({combined} {newLine}{indent}| {type}.{member})";
        }

        return $"(({type})({combined}))";
    }

    private string Call(DesignerValue value)
    {
        // CodeDOM wrote Color.FromArgb's components as ((int)(((byte)(n)))).
        if (Qualified && value is { Type: "System.Drawing.Color", Value: "FromArgb" } && value.Arguments is { Length: 3 or 4 } components
            && components.All(a => a is { Kind: DesignerValueKinds.Literal, Type: "int" or "byte" }))
        {
            return $"{Type(value.Type)}.FromArgb({string.Join(", ", components.Select(a => $"((int)(((byte)({a.Value}))))"))})";
        }

        return $"{Type(value.Type!)}.{value.Value}({Arguments(value.Arguments)})";
    }

    private string Array(DesignerValue value)
    {
        var items = value.Arguments ?? [];
        var type = Type(value.Type!);
        if (!Qualified)
        {
            return items.Length == 0 ? $"new {type}[] {{ }}" : $"new {type}[] {{ {string.Join(", ", items.Select(Value))} }}";
        }

        // CodeDOM: each item on its own line, at the statement's indentation, the brace after the last one.
        var builder = new StringBuilder($"new {type}[] {{");
        for (var i = 0; i < items.Length; i++)
        {
            builder.Append(newLine).Append(indent).Append(Value(items[i])).Append(i < items.Length - 1 ? "," : "}");
        }

        if (items.Length == 0)
        {
            builder.Append('}');
        }

        return builder.ToString();
    }

    private string Resource(DesignerValue value)
    {
        var key = SymbolDisplay.FormatLiteral(value.Value ?? string.Empty, quote: true);
        if (value.Type == "System.String")
        {
            return $"resources.GetString({key})";
        }

        if (value.Type is not { } type)
        {
            return $"resources.GetObject({key})";
        }

        return Qualified ? $"(({Type(type)})(resources.GetObject({key})))" : $"({Type(type)})resources.GetObject({key})";
    }
}
