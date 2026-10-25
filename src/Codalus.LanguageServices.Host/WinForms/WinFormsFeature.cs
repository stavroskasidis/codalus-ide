using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Location = Codalus.LanguageServices.Protocol.Location;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// The C# host's side of the Windows Forms designer (M41a, ADR 0073): <c>codalus/winforms/read</c> reads a form's
/// InitializeComponent through the project's compilation, <c>codalus/winforms/edit</c> writes the designer's changes as
/// minimal edits (and event handlers in the code file), and <c>codalus/winforms/toolbox</c> lists the components the
/// project and the projects it references declare. Reading and writing both happen here, on the same syntax, so the
/// code and the designer's model can't drift apart.
/// </summary>
internal static class WinFormsFeature
{
    private const string ControlType = "System.Windows.Forms.Control";
    private const string FormType = "System.Windows.Forms.Form";
    private const string ComponentType = "System.ComponentModel.Component";

    /// <summary>A form's code file (<c>MainForm.cs</c>) and designer file (<c>MainForm.Designer.cs</c>) from either.</summary>
    public static (string Code, string Designer) Files(string path)
    {
        path = Path.GetFullPath(path);
        var name = Path.GetFileName(path);
        if (name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return (Path.Combine(Path.GetDirectoryName(path)!, name[..^".Designer.cs".Length] + ".cs"), path);
        }

        return (path, Path.ChangeExtension(path, ".Designer.cs"));
    }

    /// <summary>What reading a form needs: the designer file's text and document, the compilation's view of its class.</summary>
    private sealed class Context
    {
        public required string CodePath { get; init; }

        public required string DesignerPath { get; init; }

        public required SourceText DesignerText { get; init; }

        public Document? Designer { get; init; }

        public SyntaxTree? CodeTree { get; init; }

        public Document? Code { get; init; }

        public INamedTypeSymbol? Class { get; init; }

        public required string BaseType { get; init; }

        public bool IsDesignable { get; init; }

        public string? NotDesignable { get; init; }

        public required string[] GlobalUsings { get; init; }

        public required ReplayDesignerTypeResolver Resolver { get; init; }

        /// <summary>
        /// The solution the form was read from: the request's, or its own copy with the code file's part of the class in
        /// Miscellaneous Files (#76). A rename goes through it, so the code file's uses are renamed too.
        /// </summary>
        public required Solution Solution { get; init; }

        /// <summary>
        /// Read without all of the project's types (#76): from Miscellaneous Files, from a compilation without Windows
        /// Forms (before the restore), or without a compilation; the class's base type then came from its syntax.
        /// </summary>
        public bool WithoutProjectTypes { get; init; }

        public CSharpParseOptions? ParseOptions { get; init; }

        /// <summary>The designer file's tree as the compilation has it.</summary>
        public required SyntaxTree DesignerTree { get; init; }

        public DesignerSource Read(SourceText text) =>
            DesignerReader.Read(text.ContentEquals(DesignerText) ? DesignerTree : CSharpSyntaxTree.ParseText(text, ParseOptions, DesignerPath), BaseType, GlobalUsings, Resolver);
    }

    /// <summary>
    /// A file's text as the designer sent it (<paramref name="held"/>: its unsaved <c>.resx</c> files), else as the editor
    /// has it, else as it is on disk (null when there's no such file).
    /// </summary>
    private static string? ReadText(string path, Func<string, SourceText?> openText, DesignerFileText[]? held = null)
    {
        if (held?.FirstOrDefault(f => string.Equals(Path.GetFullPath(f.Path), Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) is { } file)
        {
            return file.Text;
        }

        if (openText(path) is { } open)
        {
            return open.ToString();
        }

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<SourceText?> TextOfAsync(Document? document, string path, Func<string, SourceText?> openText, CancellationToken cancellationToken)
    {
        if (openText(path) is { } open)
        {
            return open;
        }

        if (document is not null)
        {
            return await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(path) ? SourceText.From(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : null;
    }

    private static async Task<Context?> ContextAsync(Solution solution, string path, Func<string, SourceText?> openText, CancellationToken cancellationToken)
    {
        var (codePath, designerPath) = Files(path);
        var designer = await WorkspaceManager.FindDocumentAsync(solution, DocumentUris.FromPath(designerPath), cancellationToken).ConfigureAwait(false);
        var code = await WorkspaceManager.FindDocumentAsync(solution, DocumentUris.FromPath(codePath), cancellationToken).ConfigureAwait(false);
        if (await TextOfAsync(designer, designerPath, openText, cancellationToken).ConfigureAwait(false) is not { } designerText)
        {
            return null;
        }

        var codeText = await TextOfAsync(code, codePath, openText, cancellationToken).ConfigureAwait(false);
        if (designer is not null && code is null && codeText is not null && WorkspaceManager.IsMiscellaneous(designer.Project))
        {
            // The designer opened the form before the C# host had its project, with the code closed (#76): alone in
            // Miscellaneous Files, the designer file's part of the class names no base, so its base was object and the
            // form "isn't a form". The class's other part joins Miscellaneous Files here, in this request's own copy of
            // the solution, as it does when the code is open: the base then comes from the code file (by name, without
            // Windows Forms' references), and a rename finds the uses in the code file. The copy never goes back to the
            // workspace, so diagnostics and the real Miscellaneous Files don't change.
            var codeId = DocumentId.CreateNewId(designer.Project.Id, codePath);
            solution = solution.AddDocument(codeId, Path.GetFileName(codePath), codeText, filePath: codePath);
            designer = solution.GetDocument(designer.Id);
            code = solution.GetDocument(codeId);
        }

        var parseOptions = designer?.Project.ParseOptions as CSharpParseOptions;
        var codeTree = codeText is null ? null : CSharpSyntaxTree.ParseText(codeText, parseOptions, codePath, cancellationToken);
        var designerTree = designer is not null && await designer.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } tree && (await tree.GetTextAsync(cancellationToken).ConfigureAwait(false)).ContentEquals(designerText)
            ? tree
            : CSharpSyntaxTree.ParseText(designerText, parseOptions, designerPath, cancellationToken);
        var found = DesignerSyntax.FindInitializeComponent(await designerTree.GetRootAsync(cancellationToken).ConfigureAwait(false));
        var resolver = new ReplayDesignerTypeResolver();
        string[] globalUsings = DesignerReader.WindowsFormsImplicitUsings;
        INamedTypeSymbol? type = null;
        string? baseType = null;
        var withoutProjectTypes = true;
        if (designer is not null && found is var (classSyntax, _) && await designer.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is { } model
            && model.SyntaxTree == designerTree)
        {
            resolver.Semantic = new SemanticDesignerTypeResolver(model);
            resolver.SemanticTree = designerTree;
            type = model.GetDeclaredSymbol(classSyntax, cancellationToken);
            baseType = type?.BaseType is { TypeKind: not TypeKind.Error } baseSymbol ? SemanticDesignerTypeResolver.Name(baseSymbol) : null;
            withoutProjectTypes = baseType is null || model.Compilation.GetTypeByMetadataName(ControlType) is null;
            IEnumerable<string> compilationUsings = model.Compilation.SyntaxTrees
                .SelectMany(t => t.GetCompilationUnitRoot(cancellationToken).Usings)
                .Where(u => !u.GlobalKeyword.IsKind(SyntaxKind.None) && u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
                .Select(u => u.Name!.ToString().Replace("global::", string.Empty, StringComparison.Ordinal));

            // Miscellaneous Files' global usings aren't the project's (#76): while the project loads, the SDK's implicit
            // usings for Windows Forms stay, as without a compilation, so new code keeps the file's short names.
            globalUsings = [.. (WorkspaceManager.IsMiscellaneous(designer.Project) ? DesignerReader.WindowsFormsImplicitUsings.Concat(compilationUsings) : compilationUsings)
                .Distinct(StringComparer.Ordinal)];
        }

        baseType ??= SyntacticBaseType(codeTree, designerTree, found?.Class.Identifier.Text) ?? FormType;
        // Without the framework's references (a project not restored yet), the base type's name decides.
        var (designable, why) = Designable(type is { BaseType.TypeKind: not TypeKind.Error } ? type : null, baseType);
        return new Context
        {
            CodePath = codePath,
            DesignerPath = designerPath,
            DesignerText = designerText,
            Designer = designer,
            Code = code,
            CodeTree = codeTree,
            Class = type,
            BaseType = baseType,
            IsDesignable = designable,
            NotDesignable = why,
            GlobalUsings = globalUsings,
            Resolver = resolver,
            Solution = solution,
            WithoutProjectTypes = withoutProjectTypes,
            ParseOptions = parseOptions,
            DesignerTree = designerTree,
        };
    }

    private static string? SyntacticBaseType(SyntaxTree? codeTree, SyntaxTree designerTree, string? className)
    {
        foreach (var tree in new[] { codeTree, designerTree })
        {
            if (tree?.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className && c.BaseList is not null) is { } declaration
                && declaration.BaseList!.Types.FirstOrDefault()?.Type is { } baseSyntax)
            {
                return new SyntacticDesignerTypeResolver(DesignerSyntax.NamespaceOf(declaration)).FullName(baseSyntax);
            }
        }

        return null;
    }

    private static (bool, string?) Designable(INamedTypeSymbol? type, string baseType)
    {
        if (type is not null)
        {
            if (InheritsFrom(type, ControlType))
            {
                return (true, null);
            }

            return InheritsFrom(type, ComponentType)
                ? (false, $"{type.Name} is a component, not a form or a control: the component designer isn't available yet, so it opens as code.")
                : (false, $"{type.Name} isn't a form, a user control or another control, so it opens as code.");
        }

        var simple = baseType[(baseType.LastIndexOf('.') + 1)..];
        return simple.EndsWith("Form", StringComparison.Ordinal) || simple.EndsWith("Control", StringComparison.Ordinal) || simple is "Panel" or "Button"
            ? (true, null)
            : (false, $"Its base type, {baseType}, isn't known to be a form or a control.");
    }

    private static bool InheritsFrom(ITypeSymbol type, string fullName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SemanticDesignerTypeResolver.Name(current) == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A form's designer model, or why it opens as code.</summary>
    public static async Task<WinFormsReadResult> ReadAsync(Solution solution, string path, Func<string, SourceText?> openText, CancellationToken cancellationToken, DesignerFileText[]? resourceFiles = null)
    {
        var (codePath, designerPath) = Files(path);
        var resx = Path.ChangeExtension(codePath, ".resx");
        var context = await ContextAsync(solution, path, openText, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new WinFormsReadResult(codePath, null, false, null, $"{Path.GetFileName(designerPath)} doesn't exist: there's no designer code to open.", null);
        }

        var project = (context.Designer ?? context.Code)?.Project.FilePath;
        var fullName = context.Class?.ToDisplayString() ?? null;
        var resources = new DesignerResourceSet(codePath, p => ReadText(p, openText, resourceFiles), resourceFiles?.Select(f => f.Path));
        var trayLocations = resources.Get(null)?.TrayLocations() ?? new Dictionary<string, (int X, int Y)>();
        WinFormsReadResult Result(DesignerModel? model, string? problem, int? line) =>
            new(codePath, designerPath, context.IsDesignable, model, problem, line)
            {
                // #76: types the compiler couldn't bind in the file as read were named by their syntax.
                IsPartial = context.WithoutProjectTypes || context.Resolver.SyntacticNames > 0,
                ResourceFile = File.Exists(resx) ? resx : null,
                Project = project,
                ClassFullName = fullName ?? (model is null ? null : model.Namespace is { } ns ? ns + "." + model.ClassName : model.ClassName),
                IsLocalizable = resources.Get(null)?.IsLocalizable ?? false,
                Cultures = [.. resources.Cultures],
                TrayOrder = [.. trayLocations.OrderBy(t => t.Value.Y).ThenBy(t => t.Value.X).Select(t => t.Key)],
            };

        if (!context.IsDesignable)
        {
            return Result(null, context.NotDesignable, null);
        }

        try
        {
            var source = context.Read(context.DesignerText);
            return Result(source.Model, null, null);
        }
        catch (DesignerReadException ex)
        {
            return Result(null, ex.Line is { } line
                ? $"The designer can't read InitializeComponent at line {line + 1}: {ex.Message}. Change the code back to what the designer writes, or edit it as code."
                : ex.Message, ex.Line);
        }
    }

    /// <summary>Applies the designer's edits: minimal edits of the designer file, and of the code file for new event handlers.</summary>
    public static async Task<WinFormsEditResult> EditAsync(Solution solution, WinFormsEditParams parameters, Func<string, SourceText?> openText, Func<string, int?> version, CancellationToken cancellationToken)
    {
        static WinFormsEditResult Fail(string message) => new(new WorkspaceEdit([]), null, message);
        if (DocumentUris.ToPath(parameters.TextDocument.Uri) is not { } path)
        {
            return Fail("The designer edits files on disk.");
        }

        var context = await ContextAsync(solution, path, openText, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return Fail($"{Path.GetFileName(Files(path).Designer)} doesn't exist.");
        }

        var designerVersion = version(context.DesignerPath);
        if (parameters.Version is { } expected && designerVersion is { } actual && expected != actual)
        {
            return Fail($"{Path.GetFileName(context.DesignerPath)} changed meanwhile: the designer reloads it.");
        }

        var resources = new DesignerResourceSet(context.CodePath, p => ReadText(p, openText, parameters.ResourceFiles), parameters.ResourceFiles?.Select(f => f.Path));
        var writer = new DesignerWriter(context.Read, resources);
        var text = context.DesignerText;
        var documents = new List<TextDocumentEdit>();
        Location? handlerLocation = null;
        try
        {
            var original = context.Read(text);
            foreach (var edit in parameters.Edits)
            {
                switch (edit.Kind)
                {
                    case DesignerEditKinds.AddEvent:
                    {
                        var handler = edit.Handler ?? DefaultHandler(original.Model, edit.Component, edit.Event!);
                        var (parameterList, delegateType) = await HandlerSignatureAsync(context, original.Model, edit, cancellationToken).ConfigureAwait(false);
                        text = writer.Apply(text, edit with { Handler = handler, DelegateType = edit.DelegateType ?? delegateType });
                        var (codeEdit, location) = await EnsureHandlerAsync(context, openText, version, original.Model.ClassName, handler, parameterList, cancellationToken).ConfigureAwait(false);
                        handlerLocation = location;
                        if (codeEdit is not null)
                        {
                            documents.RemoveAll(d => d.TextDocument.Uri == codeEdit.TextDocument.Uri);
                            documents.Add(codeEdit);
                        }

                        break;
                    }

                    case DesignerEditKinds.RenameComponent:
                        text = writer.Apply(text, edit);
                        documents.AddRange(await RenameElsewhereAsync(context, edit.Component, edit.NewName!, openText, version, cancellationToken).ConfigureAwait(false));
                        break;

                    default:
                        text = writer.Apply(text, edit);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is DesignerReadException or InvalidOperationException or ArgumentException)
        {
            return Fail(ex.Message);
        }

        var changes = LineDiff.Compute(context.DesignerText, text);
        if (changes.Count > 0)
        {
            documents.Insert(0, new TextDocumentEdit(
                new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(context.DesignerPath), designerVersion),
                [.. changes.Select(c => new TextEdit(Positions.ToRange(context.DesignerText, c.Span), c.NewText ?? string.Empty))]));
        }

        // The form's resource files (a localizable form's properties, images, a new culture's file).
        foreach (var (resxPath, before, resxText) in resources.Changes)
        {
            var old = SourceText.From(before ?? string.Empty);
            var resxChanges = LineDiff.Compute(old, SourceText.From(resxText));
            documents.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(resxPath), before is null ? null : version(resxPath)),
                [.. resxChanges.Select(c => new TextEdit(Positions.ToRange(old, c.Span), c.NewText ?? string.Empty))]));
        }

        DesignerModel? after;
        try
        {
            after = context.Read(text).Model;
        }
        catch (DesignerReadException)
        {
            after = null;
        }

        return new WinFormsEditResult(new WorkspaceEdit([.. documents]), after, null) { Handler = handlerLocation };
    }

    /// <summary>The classic IDE's name for a new handler: <c>button1_Click</c>, <c>MainForm_Load</c>.</summary>
    public static string DefaultHandler(DesignerModel model, string component, string eventName) =>
        $"{(component.Length == 0 ? model.ClassName : component)}_{eventName}";

    /// <summary>The handler's parameters (<c>object? sender</c>, <c>EventArgs e</c>) and the event's delegate type, from the caller or the compilation.</summary>
    private static async Task<(string[] Parameters, string? DelegateType)> HandlerSignatureAsync(Context context, DesignerModel model, DesignerEdit edit, CancellationToken cancellationToken)
    {
        // Nullable annotations as the code file has them: the project's, unless the file turns them off.
        var nullable = context.Designer?.Project.CompilationOptions?.NullableContextOptions is { } options && options != NullableContextOptions.Disable
            && !(context.CodeTree?.GetRoot(cancellationToken).DescendantTrivia().Any(t => t.GetStructure() is NullableDirectiveTriviaSyntax { SettingToken.RawKind: (int)SyntaxKind.DisableKeyword }) ?? false);
        string? delegateType = null;
        List<(string Type, string Name)>? parameters = edit.HandlerParameters?.Select(p => p.Split(' ', 2)).Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        if (parameters is null && context.Designer is not null)
        {
            var componentType = edit.Component.Length == 0 ? model.BaseType : model.Components.FirstOrDefault(c => c.Name == edit.Component)?.Type;
            var compilation = await context.Designer.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (componentType is not null && compilation?.GetTypeByMetadataName(componentType) is { } type)
            {
                for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
                {
                    if (current.GetMembers(edit.Event!).OfType<IEventSymbol>().FirstOrDefault() is { Type: INamedTypeSymbol { DelegateInvokeMethod: { } invoke } eventType })
                    {
                        delegateType = SemanticDesignerTypeResolver.Name(eventType);
                        parameters = [.. invoke.Parameters.Select(p => (SemanticDesignerTypeResolver.Name(p.Type), p.Name))];
                        break;
                    }
                }
            }
        }

        parameters ??= [("System.Object", "sender"), ("System.EventArgs", "e")];
        var codeModel = new DesignerModel
        {
            ClassName = model.ClassName,
            Namespace = model.Namespace,
            BaseType = model.BaseType,
            Imports = [.. (context.CodeTree is null ? [] : DesignerSyntax.UsingsOf(context.CodeTree.GetRoot(cancellationToken))).Concat(context.GlobalUsings).Distinct(StringComparer.Ordinal)],
        };
        var generator = new DesignerCodeGenerator(codeModel, string.Empty, "\n");
        var rendered = parameters.Select(p => $"{generator.Type(p.Type)}{(nullable && p.Type == "System.Object" ? "?" : string.Empty)} {p.Name}").ToArray();
        return (rendered, delegateType);
    }

    /// <summary>
    /// The handler in the code file: where it is when the class has it, else a new empty method at the end of the
    /// class (the classic IDE's shape, with an empty line in its body, where the caret goes).
    /// </summary>
    private static async Task<(TextDocumentEdit? Edit, Location? Location)> EnsureHandlerAsync(
        Context context, Func<string, SourceText?> openText, Func<string, int?> version, string className, string handler, string[] parameters, CancellationToken cancellationToken)
    {
        foreach (var path in new[] { context.CodePath, context.DesignerPath })
        {
            var text = path == context.CodePath ? await TextOfAsync(context.Code, path, openText, cancellationToken).ConfigureAwait(false) : context.DesignerText;
            if (text is null)
            {
                continue;
            }

            var root = await CSharpSyntaxTree.ParseText(text, context.ParseOptions, path, cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
            var method = root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == className)
                .SelectMany(c => c.Members.OfType<MethodDeclarationSyntax>()).FirstOrDefault(m => m.Identifier.Text == handler);
            if (method is not null)
            {
                var at = method.Body is { Statements.Count: > 0 } body ? body.Statements[0].SpanStart : method.Identifier.SpanStart;
                return (null, new Location(DocumentUris.FromPath(path), Positions.ToRange(text, new TextSpan(at, 0))));
            }
        }

        if (await TextOfAsync(context.Code, context.CodePath, openText, cancellationToken).ConfigureAwait(false) is not { } codeText)
        {
            throw new InvalidOperationException($"{Path.GetFileName(context.CodePath)} doesn't exist: the event handler goes there.");
        }

        var codeRoot = await CSharpSyntaxTree.ParseText(codeText, context.ParseOptions, context.CodePath, cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (codeRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className) is not { } type)
        {
            throw new InvalidOperationException($"{Path.GetFileName(context.CodePath)} doesn't declare {className}: the event handler goes there.");
        }

        var newLine = codeText.Lines.Count > 1 && codeText.Lines[0].EndIncludingLineBreak - codeText.Lines[0].End == 2 ? "\r\n" : "\n";
        var classIndent = Indent(codeText, type.SpanStart);
        var indent = type.Members.FirstOrDefault() is { } member ? Indent(codeText, member.SpanStart) : classIndent + (classIndent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
        var bodyIndent = indent + (indent.Contains('\t', StringComparison.Ordinal) ? "\t" : "    ");
        var closeLine = codeText.Lines.GetLineFromPosition(type.CloseBraceToken.SpanStart);
        var lead = type.Members.Count > 0 ? newLine : string.Empty;
        var method2 = $"{lead}{indent}private void {handler}({string.Join(", ", parameters)}){newLine}{indent}{{{newLine}{newLine}{indent}}}{newLine}";
        var insertAt = closeLine.Start;
        var caretLine = closeLine.LineNumber + (lead.Length > 0 ? 3 : 2);
        var edit = new TextDocumentEdit(
            new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(context.CodePath), version(context.CodePath)),
            [new TextEdit(Positions.ToRange(codeText, new TextSpan(insertAt, 0)), method2)]);
        var position = new Position(caretLine, bodyIndent.Length);
        return (edit, new Location(DocumentUris.FromPath(context.CodePath), new Protocol.Range(position, position)));
    }

    private static string Indent(SourceText text, int position)
    {
        var line = text.Lines.GetLineFromPosition(position).ToString();
        return line[..(line.Length - line.TrimStart().Length)];
    }

    /// <summary>
    /// A renamed component's uses outside the designer file, through the compilation (Roslyn's rename of the field) of
    /// the solution the form was read from: with the code file's part added while the project loads (#76), its uses
    /// there are renamed too (with no version: the file isn't open).
    /// </summary>
    private static async Task<IEnumerable<TextDocumentEdit>> RenameElsewhereAsync(
        Context context, string name, string newName, Func<string, SourceText?> openText, Func<string, int?> version, CancellationToken cancellationToken)
    {
        if (context.Class?.GetMembers(name).OfType<IFieldSymbol>().FirstOrDefault() is not { } field)
        {
            return [];
        }

        var solution = context.Solution;
        var renamed = await Renamer.RenameSymbolAsync(solution, field, new SymbolRenameOptions(), newName, cancellationToken).ConfigureAwait(false);
        var edits = new List<TextDocumentEdit>();
        foreach (var projectChanges in renamed.GetChanges(solution).GetProjectChanges())
        {
            foreach (var id in projectChanges.GetChangedDocuments())
            {
                var before = solution.GetDocument(id);
                var after = renamed.GetDocument(id);
                if (before?.FilePath is not { } path || after is null || Paths.Comparer.Equals(path, context.DesignerPath))
                {
                    continue;
                }

                var oldText = await before.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (openText(path) is { } open && !open.ContentEquals(oldText))
                {
                    continue;
                }

                var newText = await after.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var changes = LineDiff.Compute(oldText, newText);
                if (changes.Count > 0)
                {
                    edits.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), version(path)),
                        [.. changes.Select(c => new TextEdit(Positions.ToRange(oldText, c.Span), c.NewText ?? string.Empty))]));
                }
            }
        }

        return edits;
    }

    /// <summary>The components of a project and of the projects it references, for the Toolbox (<c>[ToolboxItem(false)]</c> and forms left out).</summary>
    public static async Task<WinFormsToolboxResult> ToolboxAsync(Solution solution, string projectPath, CancellationToken cancellationToken)
    {
        var project = solution.Projects.FirstOrDefault(p => p.FilePath is { } path && Paths.Comparer.Equals(path, Path.GetFullPath(projectPath)) && p.Language == LanguageNames.CSharp);
        if (project is null)
        {
            return new WinFormsToolboxResult([]);
        }

        var items = new List<WinFormsToolboxItem>();
        var projects = new[] { project }.Concat(project.ProjectReferences.Select(r => solution.GetProject(r.ProjectId)).OfType<Project>()).DistinctBy(p => p.Id);
        foreach (var candidate in projects)
        {
            if (await candidate.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }

            var own = candidate.Id == project.Id;
            foreach (var type in Types(compilation.Assembly.GlobalNamespace))
            {
                if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false, IsStatic: false }
                    || !(type.DeclaredAccessibility == Accessibility.Public || (own && type.DeclaredAccessibility == Accessibility.Internal))
                    || !InheritsFrom(type, ComponentType) || InheritsFrom(type, FormType)
                    || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                {
                    continue;
                }

                var attributes = type.GetAttributes();
                if (attributes.Any(a => a.AttributeClass?.Name is "ToolboxItemAttribute" && a.ConstructorArguments is [{ Value: false }])
                    || attributes.Any(a => a.AttributeClass?.Name is "DesignTimeVisibleAttribute" && a.ConstructorArguments is [{ Value: false }]))
                {
                    continue;
                }

                var description = attributes.FirstOrDefault(a => a.AttributeClass?.Name is "DescriptionAttribute")?.ConstructorArguments.FirstOrDefault().Value as string;
                items.Add(new WinFormsToolboxItem(SemanticDesignerTypeResolver.Name(type), type.Name, candidate.Name, InheritsFrom(type, ControlType)) { Description = description });
            }
        }

        return new WinFormsToolboxResult([.. items.OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol child)
            {
                foreach (var type in Types(child))
                {
                    yield return type;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                yield return type;
                foreach (var nested in type.GetTypeMembers())
                {
                    yield return nested;
                }
            }
        }
    }
}

/// <summary>
/// Names types through the compilation on the first read of a file, and remembers what each written name meant, so
/// the writer's reads of the edited text (which the compilation hasn't seen) name them the same way.
/// </summary>
internal sealed class ReplayDesignerTypeResolver : IDesignerTypeResolver
{
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _enums = new(StringComparer.Ordinal);

    public SemanticDesignerTypeResolver? Semantic { get; set; }

    /// <summary>The tree <see cref="Semantic"/>'s model is of; other trees (edited text) replay what it said.</summary>
    public SyntaxTree? SemanticTree { get; set; }

    public SyntacticDesignerTypeResolver? Fallback { get; set; }

    /// <summary>
    /// How many types of the file as read the compiler couldn't bind, so their syntax named them (#76: the read lacked
    /// the project's types); names replayed for edited text don't count.
    /// </summary>
    public int SyntacticNames { get; private set; }

    public string? FullName(SyntaxNode node)
    {
        var written = string.Concat(node.ToString().Where(c => !char.IsWhiteSpace(c)));
        if (node.SyntaxTree == SemanticTree && Semantic?.FullName(node) is { } resolved)
        {
            _names[written] = resolved;
            if (Semantic.IsEnum(resolved) is { } isEnum)
            {
                _enums[resolved] = isEnum;
            }

            return resolved;
        }

        if (_names.TryGetValue(written, out var known))
        {
            return known;
        }

        if (node.SyntaxTree == SemanticTree)
        {
            SyntacticNames++;
        }

        var fallback = Fallback ?? new SyntacticDesignerTypeResolver(DesignerSyntax.NamespaceOf(node));
        return fallback.FullName(node);
    }

    public bool? IsEnum(string fullName) =>
        Semantic?.IsEnum(fullName) ?? (_enums.TryGetValue(fullName, out var isEnum) ? isEnum : new SyntacticDesignerTypeResolver(null).IsEnum(fullName));
}
