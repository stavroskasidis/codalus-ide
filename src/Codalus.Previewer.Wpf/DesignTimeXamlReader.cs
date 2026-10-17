using System.Windows;
using System.Xaml;

namespace Codalus.Previewer.Wpf;

/// <summary>
/// The edited XAML's node stream as a designer loads it (M40b, ADR 0072): WPF's loader refuses what only the markup
/// compiler can wire up, so this leaves out the <c>x:Class</c>, <c>x:Subclass</c>, <c>x:ClassModifier</c> and
/// <c>x:FieldModifier</c> directives, <c>x:Code</c>, event handlers (<c>Click="OnClick"</c>, attached events) and
/// <c>EventSetter</c>s. Everything else, line information included, passes through to <c>XamlReader.Load</c>.
/// </summary>
internal sealed class DesignTimeXamlReader(XamlXmlReader inner) : XamlReader, IXamlLineInfo
{
    private static readonly HashSet<XamlMember> s_directives =
    [
        XamlLanguage.Class, XamlLanguage.Subclass, XamlLanguage.ClassModifier, XamlLanguage.FieldModifier, XamlLanguage.Code,
    ];

    public override bool Read()
    {
        if (!inner.Read())
        {
            return false;
        }

        while (DesignOnly())
        {
            // Past the member's or object's end, then on to the node after it (which may be skipped as well).
            var depth = 1;
            var start = inner.NodeType;
            while (depth > 0 && inner.Read())
            {
                if (start == XamlNodeType.StartMember)
                {
                    depth += inner.NodeType switch { XamlNodeType.StartMember => 1, XamlNodeType.EndMember => -1, _ => 0 };
                }
                else
                {
                    depth += inner.NodeType switch { XamlNodeType.StartObject or XamlNodeType.GetObject => 1, XamlNodeType.EndObject => -1, _ => 0 };
                }
            }

            if (!inner.Read())
            {
                return false;
            }
        }

        return true;
    }

    private bool DesignOnly() => inner.NodeType switch
    {
        XamlNodeType.StartMember => inner.Member is { } member && (member.IsEvent || s_directives.Contains(member)),
        XamlNodeType.StartObject => inner.Type?.UnderlyingType == typeof(EventSetter),
        _ => false,
    };

    public override XamlNodeType NodeType => inner.NodeType;

    public override bool IsEof => inner.IsEof;

    public override NamespaceDeclaration Namespace => inner.Namespace;

    public override XamlSchemaContext SchemaContext => inner.SchemaContext;

    public override XamlType Type => inner.Type;

    public override object Value => inner.Value;

    public override XamlMember Member => inner.Member;

    public bool HasLineInfo => inner.HasLineInfo;

    public int LineNumber => inner.LineNumber;

    public int LinePosition => inner.LinePosition;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ((IDisposable)inner).Dispose();
        }

        base.Dispose(disposing);
    }
}
