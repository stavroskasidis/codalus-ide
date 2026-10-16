using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>Names the types a designer file writes by their full C# names (M41a, ADR 0073).</summary>
internal interface IDesignerTypeResolver
{
    /// <summary>The full name of the type <paramref name="node"/> (a type or a name) refers to, or null when it isn't a type.</summary>
    string? FullName(SyntaxNode node);

    /// <summary>Whether <paramref name="fullName"/> is an enum (null when unknown).</summary>
    bool? IsEnum(string fullName);
}

/// <summary>Through the project's compilation: what the compiler binds the name to.</summary>
internal sealed class SemanticDesignerTypeResolver(SemanticModel model) : IDesignerTypeResolver
{
    private static readonly SymbolDisplayFormat s_format = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable);

    private readonly Dictionary<string, ITypeSymbol> _seen = new(StringComparer.Ordinal);

    public string? FullName(SyntaxNode node)
    {
        var symbol = model.GetSymbolInfo(node).Symbol ?? model.GetTypeInfo(node).Type;
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is not ITypeSymbol { TypeKind: not TypeKind.Error } type)
        {
            return null;
        }

        var name = Name(type);
        _seen[name] = type;
        return name;
    }

    public bool? IsEnum(string fullName) => _seen.TryGetValue(fullName, out var type) ? type.TypeKind == TypeKind.Enum : null;

    public static string Name(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Object => "System.Object",
        SpecialType.System_String => "System.String",
        SpecialType.System_Boolean => "System.Boolean",
        SpecialType.System_Char => "System.Char",
        SpecialType.System_Byte => "System.Byte",
        SpecialType.System_Int16 => "System.Int16",
        SpecialType.System_Int32 => "System.Int32",
        SpecialType.System_Int64 => "System.Int64",
        SpecialType.System_Single => "System.Single",
        SpecialType.System_Double => "System.Double",
        SpecialType.System_Decimal => "System.Decimal",
        _ => type.ToDisplayString(s_format),
    };
}

/// <summary>
/// Without a compilation (tests, a file outside a project): the common types of Windows Forms, System.Drawing and
/// System.ComponentModel by name, names starting with <c>System.</c> as written, and others in the file's namespace.
/// </summary>
internal sealed class SyntacticDesignerTypeResolver(string? fileNamespace) : IDesignerTypeResolver
{
    private static readonly Dictionary<string, string> s_keywords = new(StringComparer.Ordinal)
    {
        ["object"] = "System.Object", ["string"] = "System.String", ["bool"] = "System.Boolean", ["char"] = "System.Char",
        ["byte"] = "System.Byte", ["short"] = "System.Int16", ["int"] = "System.Int32", ["long"] = "System.Int64",
        ["float"] = "System.Single", ["double"] = "System.Double", ["decimal"] = "System.Decimal",
    };

    /// <summary>Common types by namespace (the Toolbox's, and the values Visual Studio writes).</summary>
    private static readonly (string Namespace, string[] Types)[] s_known =
    [
        ("System.Windows.Forms",
        [
            "AnchorStyles", "Appearance", "AutoScaleMode", "AutoSizeMode", "BindingNavigator", "BorderStyle", "Button", "ButtonBase", "CheckBox",
            "CheckedListBox", "CheckState", "ColorDialog", "ColumnHeader", "ColumnStyle", "ComboBox", "ComboBoxStyle", "ContentAlignment",
            "ContextMenuStrip", "Control", "Cursors", "DataGridView", "DataGridViewAutoSizeColumnsMode", "DataGridViewCellStyle",
            "DataGridViewColumnHeadersHeightSizeMode", "DataGridViewTextBoxColumn", "DataGridViewColumn", "DateTimePicker", "DateTimePickerFormat",
            "DialogResult", "DockStyle", "ErrorProvider", "FlatStyle", "FlowDirection", "FlowLayoutPanel", "FolderBrowserDialog", "FontDialog",
            "Form", "FormBorderStyle", "FormStartPosition", "FormWindowState", "GroupBox", "HelpProvider", "HorizontalAlignment", "ImageLayout",
            "ImageList", "Keys", "Label", "LinkLabel", "ListBox", "ListView", "ListViewItem", "MaskedTextBox", "MenuStrip", "MonthCalendar",
            "NotifyIcon", "NumericUpDown", "OpenFileDialog", "Orientation", "Padding", "PageSetupDialog", "Panel", "PictureBox",
            "PictureBoxSizeMode", "PrintDialog", "PrintPreviewControl", "PrintPreviewDialog", "ProgressBar", "ProgressBarStyle", "RadioButton",
            "RichTextBox", "RightToLeft", "RowStyle", "SaveFileDialog", "ScrollBars", "SelectionMode", "SizeType", "SplitContainer",
            "StatusStrip", "TabAlignment", "TabControl", "TableLayoutPanel", "TableLayoutPanelCellBorderStyle", "TabPage", "TextBox",
            "TextImageRelation", "Timer", "ToolStrip", "ToolStripButton", "ToolStripContainer", "ToolStripItem", "ToolStripMenuItem",
            "ToolStripSeparator", "ToolStripStatusLabel", "ToolTip", "TrackBar", "TreeNode", "TreeView", "UserControl", "View", "WebBrowser",
        ]),
        ("System.Drawing", ["Color", "ContentAlignment", "Font", "FontStyle", "GraphicsUnit", "Icon", "Image", "Bitmap", "Point", "PointF", "Rectangle", "Size", "SizeF", "SystemColors", "SystemFonts"]),
        ("System.ComponentModel", ["BackgroundWorker", "ComponentResourceManager", "Container", "IContainer", "ISupportInitialize"]),
        ("System.Drawing.Printing", ["PrintDocument"]),
        ("System.Diagnostics", ["Process"]),
        ("System.IO", ["FileSystemWatcher"]),
    ];

    private static readonly HashSet<string> s_enums = new(StringComparer.Ordinal)
    {
        "System.Windows.Forms.AnchorStyles", "System.Windows.Forms.Appearance", "System.Windows.Forms.AutoScaleMode", "System.Windows.Forms.AutoSizeMode",
        "System.Windows.Forms.BorderStyle", "System.Windows.Forms.CheckState", "System.Windows.Forms.ComboBoxStyle", "System.Drawing.ContentAlignment",
        "System.Windows.Forms.DateTimePickerFormat", "System.Windows.Forms.DialogResult", "System.Windows.Forms.DockStyle", "System.Windows.Forms.FlatStyle",
        "System.Windows.Forms.FlowDirection", "System.Windows.Forms.FormBorderStyle", "System.Windows.Forms.FormStartPosition", "System.Windows.Forms.FormWindowState",
        "System.Windows.Forms.HorizontalAlignment", "System.Windows.Forms.ImageLayout", "System.Windows.Forms.Keys", "System.Windows.Forms.Orientation",
        "System.Windows.Forms.PictureBoxSizeMode", "System.Windows.Forms.ProgressBarStyle", "System.Windows.Forms.RightToLeft", "System.Windows.Forms.ScrollBars",
        "System.Windows.Forms.SelectionMode", "System.Windows.Forms.SizeType", "System.Windows.Forms.TabAlignment", "System.Windows.Forms.TextImageRelation",
        "System.Windows.Forms.View", "System.Drawing.FontStyle", "System.Drawing.GraphicsUnit", "System.Windows.Forms.DataGridViewAutoSizeColumnsMode",
        "System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode", "System.Windows.Forms.TableLayoutPanelCellBorderStyle",
    };

    private static readonly Dictionary<string, string> s_byName = s_known
        .SelectMany(k => k.Types.Select(t => (t, k.Namespace)))
        .GroupBy(p => p.t, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Namespace + "." + g.Key, StringComparer.Ordinal);

    public string? FullName(SyntaxNode node)
    {
        var text = node switch
        {
            PredefinedTypeSyntax predefined => predefined.Keyword.Text,
            TypeSyntax or MemberAccessExpressionSyntax => Strip(node.ToString()),
            _ => null,
        };
        if (text is null)
        {
            return null;
        }

        if (s_keywords.TryGetValue(text, out var keyword))
        {
            return keyword;
        }

        if (text.StartsWith("System.", StringComparison.Ordinal) || text.StartsWith("Microsoft.", StringComparison.Ordinal))
        {
            return text;
        }

        if (!text.Contains('.', StringComparison.Ordinal) && s_byName.TryGetValue(text, out var known))
        {
            return known;
        }

        // Another type: the file's own namespace's (a user control, Properties.Resources).
        return fileNamespace is { Length: > 0 } ns ? ns + "." + text : text;
    }

    public bool? IsEnum(string fullName) => s_enums.Contains(fullName) ? true : null;

    private static string Strip(string text)
    {
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        return text.StartsWith("global::", StringComparison.Ordinal) ? text["global::".Length..] : text;
    }
}

/// <summary>Helpers over the syntax of designer files.</summary>
internal static class DesignerSyntax
{
    /// <summary>The class (of the file's top-level classes and their nested ones) whose body declares <c>InitializeComponent()</c>.</summary>
    public static (ClassDeclarationSyntax Class, MethodDeclarationSyntax Method)? FindInitializeComponent(SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or ClassDeclarationSyntax).OfType<ClassDeclarationSyntax>())
        {
            if (type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "InitializeComponent" && m.ParameterList.Parameters.Count == 0) is { } method)
            {
                return (type, method);
            }
        }

        return null;
    }

    /// <summary>The namespace a type is declared in (nested namespaces joined), or null.</summary>
    public static string? NamespaceOf(SyntaxNode node) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()) is var parts && parts.Any()
            ? string.Join('.', parts)
            : null;

    /// <summary>The <c>using</c> namespaces of the file (not aliases or statics), with <c>global using</c>s.</summary>
    public static IEnumerable<string> UsingsOf(SyntaxNode root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax).OfType<UsingDirectiveSyntax>()
            .Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
            .Select(u => u.Name!.ToString().Replace("global::", string.Empty, StringComparison.Ordinal));
}
