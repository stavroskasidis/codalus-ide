using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;
using Location = Codalus.LanguageServices.Protocol.Location;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace Codalus.LanguageServices.Host.WinForms;

/// <summary>
/// The C# host's side of the Windows Forms designer (M41a, ADR 0073): <c>codalus/winforms/read</c> reads a form's
/// InitializeComponent through the project's compilation, <c>codalus/winforms/edit</c> writes the designer's changes as
/// minimal edits (and event handlers in the code file), and <c>codalus/winforms/toolbox</c> lists the components the
/// project, the projects it references and (#454) its referenced packages declare. Reading and writing both happen here, on the same syntax, so the
/// code and the designer's model can't drift apart. A Visual Basic form (<c>Form1.vb</c>, <c>Form1.Designer.vb</c>) is read
/// and written the same way in its own syntax, its events hooked by <c>Handles</c> clauses (<see cref="VisualBasicHandles"/>).
/// </summary>
internal static class WinFormsFeature
{
    private const string ControlType = "System.Windows.Forms.Control";
    private const string FormType = "System.Windows.Forms.Form";
    private const string ComponentType = "System.ComponentModel.Component";

    /// <summary>A form's code file (<c>MainForm.cs</c>, <c>MainForm.vb</c>) and designer file (<c>MainForm.Designer.cs</c>, <c>MainForm.Designer.vb</c>) from either.</summary>
    public static (string Code, string Designer) Files(string path)
    {
        path = Path.GetFullPath(path);
        var extension = DesignerLanguages.Extension(DesignerLanguages.OfPath(path));
        var name = Path.GetFileName(path);
        if (name.EndsWith(".Designer" + extension, StringComparison.OrdinalIgnoreCase))
        {
            return (Path.Combine(Path.GetDirectoryName(path)!, name[..^(".Designer" + extension).Length] + extension), path);
        }

        return (path, Path.ChangeExtension(path, ".Designer" + extension));
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

        public ParseOptions? ParseOptions { get; init; }

        /// <summary>The designer file's tree as the compilation has it.</summary>
        public required SyntaxTree DesignerTree { get; init; }

        /// <summary>The form's language (<see cref="DesignerLanguages"/>).</summary>
        public required string Language { get; init; }

        /// <summary>A Visual Basic project's root namespace, which its classes are in without saying so.</summary>
        public string? RootNamespace { get; init; }

        public bool IsVisualBasic => Language == DesignerLanguages.VisualBasic;

        public DesignerSource Read(SourceText text) =>
            DesignerReader.Read(text.ContentEquals(DesignerText) ? DesignerTree : Parse(text, DesignerPath, ParseOptions), BaseType, GlobalUsings, Resolver, RootNamespace);

        /// <summary>The model with, for a Visual Basic form, the events its <c>Handles</c> clauses hook (in the code file's text <paramref name="code"/> and the designer file's <paramref name="designer"/>).</summary>
        public DesignerModel WithHandles(DesignerModel model, SourceText? code, SourceText designer) => IsVisualBasic
            ? VisualBasicHandles.WithHandles(model, code is null ? null : Parse(code, CodePath, ParseOptions).GetRoot(), Parse(designer, DesignerPath, ParseOptions).GetRoot())
            : model;
    }

    /// <summary>A form file's tree in its language.</summary>
    private static SyntaxTree Parse(SourceText text, string path, ParseOptions? options, CancellationToken cancellationToken = default) =>
        DesignerLanguages.OfPath(path) == DesignerLanguages.VisualBasic
            ? VisualBasicSyntaxTree.ParseText(text, options as VisualBasicParseOptions, path, cancellationToken)
            : CSharpSyntaxTree.ParseText(text, options as CSharpParseOptions, path, cancellationToken);

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

        var language = DesignerLanguages.OfPath(designerPath);
        var isVisualBasic = language == DesignerLanguages.VisualBasic;
        var parseOptions = designer?.Project.ParseOptions;
        var codeTree = codeText is null ? null : Parse(codeText, codePath, parseOptions, cancellationToken);
        var designerTree = designer is not null && await designer.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false) is { } tree && (await tree.GetTextAsync(cancellationToken).ConfigureAwait(false)).ContentEquals(designerText)
            ? tree
            : Parse(designerText, designerPath, parseOptions, cancellationToken);
        var designerRoot = await designerTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        var (classNode, className) = isVisualBasic
            ? VisualBasicDesignerSyntax.FindInitializeComponent(designerRoot) is var (vbClass, _) ? ((SyntaxNode?)vbClass.ClassStatement, vbClass.ClassStatement.Identifier.ValueText) : (null, null)
            : DesignerSyntax.FindInitializeComponent(designerRoot) is var (csClass, _) ? (csClass, csClass.Identifier.Text) : (null, null);
        var resolver = new ReplayDesignerTypeResolver();
        string[] globalUsings = isVisualBasic ? VisualBasicDesignerSyntax.WindowsFormsImplicitImports : DesignerReader.WindowsFormsImplicitUsings;
        string? rootNamespace = null;
        INamedTypeSymbol? type = null;
        string? baseType = null;
        var withoutProjectTypes = true;
        if (designer is not null && classNode is not null && await designer.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is { } model
            && model.SyntaxTree == designerTree)
        {
            resolver.Semantic = new SemanticDesignerTypeResolver(model);
            resolver.SemanticTree = designerTree;
            type = model.GetDeclaredSymbol(classNode, cancellationToken) as INamedTypeSymbol;
            baseType = type?.BaseType is { TypeKind: not TypeKind.Error } baseSymbol ? SemanticDesignerTypeResolver.Name(baseSymbol) : null;
            withoutProjectTypes = baseType is null || model.Compilation.GetTypeByMetadataName(ControlType) is null;
            if (model.Compilation.Options is VisualBasicCompilationOptions vbOptions)
            {
                // A Visual Basic project's imports are its compilation's (the SDK's and the project's <Import> items).
                rootNamespace = vbOptions.RootNamespace is { Length: > 0 } root ? root : null;
                globalUsings = [.. vbOptions.GlobalImports.Select(i => i.Clause).OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleImportsClauseSyntax>()
                    .Where(c => c.Alias is null).Select(c => VisualBasicDesignerSyntax.StripGlobal(c.Name.ToString())).Distinct(StringComparer.OrdinalIgnoreCase)];
            }
            else
            {
                IEnumerable<string> compilationUsings = model.Compilation.SyntaxTrees
                    .SelectMany(t => t.GetCompilationUnitRoot(cancellationToken).Usings)
                    .Where(u => !u.GlobalKeyword.IsKind(SyntaxKind.None) && u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
                    .Select(u => u.Name!.ToString().Replace("global::", string.Empty, StringComparison.Ordinal));

                // Miscellaneous Files' global usings aren't the project's (#76): while the project loads, the SDK's implicit
                // usings for Windows Forms stay, as without a compilation, so new code keeps the file's short names.
                globalUsings = [.. (WorkspaceManager.IsMiscellaneous(designer.Project) ? DesignerReader.WindowsFormsImplicitUsings.Concat(compilationUsings) : compilationUsings)
                    .Distinct(StringComparer.Ordinal)];
            }
        }

        baseType ??= SyntacticBaseType(codeTree, designerTree, className) ?? FormType;
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
            Language = language,
            RootNamespace = rootNamespace,
        };
    }

    private static string? SyntacticBaseType(SyntaxTree? codeTree, SyntaxTree designerTree, string? className)
    {
        foreach (var tree in new[] { codeTree, designerTree })
        {
            if (tree?.Options.Language == LanguageNames.VisualBasic)
            {
                if (tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.VisualBasic.Syntax.ClassBlockSyntax>()
                        .FirstOrDefault(c => string.Equals(c.ClassStatement.Identifier.ValueText, className, StringComparison.OrdinalIgnoreCase) && c.Inherits.Count > 0) is { } block
                    && VisualBasicDesignerSyntax.InheritsOf(block) is { } inherits)
                {
                    return new SyntacticDesignerTypeResolver(VisualBasicDesignerSyntax.NamespaceOf(block, rootNamespace: null)).FullName(inherits);
                }

                continue;
            }

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
            var codeText = context.IsVisualBasic ? await TextOfAsync(context.Code, context.CodePath, openText, cancellationToken).ConfigureAwait(false) : null;
            return Result(context.WithHandles(source.Model, codeText, context.DesignerText), null, null);
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

        // A Visual Basic form's code file as the edits change it (its handlers and their Handles clauses), diffed once at the end.
        var codeBefore = context.IsVisualBasic ? await TextOfAsync(context.Code, context.CodePath, openText, cancellationToken).ConfigureAwait(false) : null;
        var codeText = codeBefore;
        string? located = null;
        try
        {
            var original = context.Read(text);
            foreach (var edit in parameters.Edits)
            {
                if (context.IsVisualBasic && edit.Kind is DesignerEditKinds.AddEvent or DesignerEditKinds.RemoveEvent or DesignerEditKinds.RemoveComponent)
                {
                    var handler = edit.Kind == DesignerEditKinds.AddEvent ? edit.Handler ?? DefaultHandler(original.Model, edit.Component, edit.Event!) : null;
                    var signature = handler is null ? default : await HandlerSignatureAsync(context, original.Model, edit, cancellationToken).ConfigureAwait(false);
                    (text, codeText) = EditVisualBasic(context, writer, text, codeText, edit, handler, signature.Parameters, signature.DelegateType);
                    located = handler ?? located;
                    continue;
                }

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
                        foreach (var renamed in await RenameElsewhereAsync(context, edit.Component, edit.NewName!, openText, version, cancellationToken).ConfigureAwait(false))
                        {
                            // A Visual Basic form's Handles clauses in its code file are renamed with the code file's other changes.
                            if (codeText is not null && codeBefore is not null && Paths.Comparer.Equals(DocumentUris.ToPath(renamed.TextDocument.Uri) ?? string.Empty, context.CodePath) && codeText.ContentEquals(codeBefore))
                            {
                                codeText = codeText.WithChanges(renamed.Edits.Select(e => new TextChange(Positions.ToSpan(codeText, e.Range), e.NewText)));
                                continue;
                            }

                            documents.Add(renamed);
                        }

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

        if (codeText is not null && codeBefore is not null && !codeText.ContentEquals(codeBefore))
        {
            var codeChanges = LineDiff.Compute(codeBefore, codeText);
            documents.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(context.CodePath), version(context.CodePath)),
                [.. codeChanges.Select(c => new TextEdit(Positions.ToRange(codeBefore, c.Span), c.NewText ?? string.Empty))]));
        }

        if (located is not null)
        {
            // The handler, in the class's file that declares it, as the edits left it.
            foreach (var (filePath, file) in new[] { (context.CodePath, codeText), (context.DesignerPath, text) })
            {
                if (file is not null && VisualBasicHandles.HandlerPosition(file, filePath, context.ParseOptions, context.Read(text).Model.ClassName, located) is { } at)
                {
                    handlerLocation = new Location(DocumentUris.FromPath(filePath), new Protocol.Range(at, at));
                    break;
                }
            }
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
            after = context.WithHandles(context.Read(text).Model, codeText, text);
        }
        catch (DesignerReadException)
        {
            after = null;
        }

        return new WinFormsEditResult(new WorkspaceEdit([.. documents]), after, null) { Handler = handlerLocation };
    }

    /// <summary>
    /// A Visual Basic form's event edits and component removals (ADR 0073, Visual Basic forms), in its designer file's
    /// text and its code file's: a new handler hooked by a <c>Handles</c> clause (its component's field made
    /// <c>WithEvents</c> when it isn't), or, for an event hooked by <c>AddHandler</c> already and a local's, in
    /// InitializeComponent (<see cref="VisualBasicHandles.UsesHandles"/>); a removed event's or component's <c>Handles</c>
    /// items removed from both files (a method stays, as the classic IDE leaves it).
    /// </summary>
    private static (SourceText Designer, SourceText? Code) EditVisualBasic(
        Context context, DesignerWriter writer, SourceText text, SourceText? code, DesignerEdit edit, string? handler, string[]? parameters, string? delegateType)
    {
        var source = context.Read(text);
        var className = source.Model.ClassName;
        SourceText? Remove(SourceText? file, string path, Func<string, string, string, bool> which) =>
            file is null ? null : VisualBasicHandles.Remove(file, path, context.ParseOptions, className, which);
        bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        switch (edit.Kind)
        {
            case DesignerEditKinds.RemoveComponent:
            {
                var before = source.Model.Components.Where(c => !c.IsRoot).Select(c => c.Name).ToList();
                text = writer.Apply(text, edit);
                var after = context.Read(text).Model.Components.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var removed = before.Where(n => !after.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool Gone(string component, string _, string __) => component.Length > 0 && removed.Contains(component);
                return (Remove(text, context.DesignerPath, Gone)!, Remove(code, context.CodePath, Gone));
            }

            case DesignerEditKinds.RemoveEvent:
            {
                text = writer.Apply(text, edit);
                bool Hooked(string component, string eventName, string _) => Is(component, edit.Component) && Is(eventName, edit.Event!);
                return (Remove(text, context.DesignerPath, Hooked)!, Remove(code, context.CodePath, Hooked));
            }
        }

        // AddEvent.
        var eventName = edit.Event!;
        if (code is null)
        {
            throw new InvalidOperationException($"{Path.GetFileName(context.CodePath)} doesn't exist: the event handler goes there.");
        }

        if (!VisualBasicHandles.UsesHandles(source, edit.Component, eventName))
        {
            text = writer.Apply(text, edit with { Handler = handler, DelegateType = edit.DelegateType ?? delegateType });
            var declared = VisualBasicHandles.HandlerPosition(text, context.DesignerPath, context.ParseOptions, className, handler!) is not null
                || VisualBasicHandles.HandlerPosition(code, context.CodePath, context.ParseOptions, className, handler!) is not null;
            return (text, declared ? code : VisualBasicHandles.AddMethod(code, context.CodePath, context.ParseOptions, className, handler!, parameters ?? [], handles: null));
        }

        // Another method's Handles item for the event goes: an event has one handler in the designer.
        bool Other(string component, string name, string method) => Is(component, edit.Component) && Is(name, eventName) && !Is(method, handler!);
        text = Remove(VisualBasicHandles.WithEvents(source, edit.Component), context.DesignerPath, Other)!;
        code = Remove(code, context.CodePath, Other)!;
        if (VisualBasicHandles.AddTo(code, context.CodePath, context.ParseOptions, className, handler!, edit.Component, eventName) is { } inCode)
        {
            return (text, inCode);
        }

        if (VisualBasicHandles.AddTo(text, context.DesignerPath, context.ParseOptions, className, handler!, edit.Component, eventName) is { } inDesigner)
        {
            return (inDesigner, code);
        }

        return (text, VisualBasicHandles.AddMethod(code, context.CodePath, context.ParseOptions, className, handler!, parameters ?? [],
            VisualBasicHandles.ItemText(edit.Component, eventName)));
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
        if (context.IsVisualBasic)
        {
            // Visual Basic: sender As Object, e As EventArgs (through the code file's and the project's imports).
            var vbModel = new DesignerModel
            {
                ClassName = model.ClassName,
                Language = DesignerLanguages.VisualBasic,
                Namespace = model.Namespace,
                BaseType = model.BaseType,
                Imports = [.. (context.CodeTree is null ? [] : VisualBasicDesignerSyntax.ImportsOf(context.CodeTree.GetRoot(cancellationToken))).Concat(context.GlobalUsings).Distinct(StringComparer.OrdinalIgnoreCase)],
            };
            var vbGenerator = new VisualBasicDesignerCodeGenerator(vbModel, string.Empty, "\n");
            return ([.. parameters.Select(p => $"{p.Name} As {vbGenerator.Type(p.Type)}")], delegateType);
        }

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

            var root = await CSharpSyntaxTree.ParseText(text, context.ParseOptions as CSharpParseOptions, path, cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
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

        var codeRoot = await CSharpSyntaxTree.ParseText(codeText, context.ParseOptions as CSharpParseOptions, context.CodePath, cancellationToken).GetRootAsync(cancellationToken).ConfigureAwait(false);
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
        // A Visual Basic WithEvents field is a property to the compiler (renaming it renames its Handles clauses too).
        if (context.Class?.GetMembers(name).FirstOrDefault(m => m is IFieldSymbol or IPropertySymbol { IsWithEvents: true }) is not { } field)
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

    /// <summary>
    /// The components of a project and of the projects it references, then (#454) those of the assemblies it references
    /// (its packages', a control library's), for the Toolbox: <c>[ToolboxItem(false)]</c>, <c>[DesignTimeVisible(false)]</c>
    /// (their own or a base type's, as Windows Forms reads them) and forms left out. A referenced assembly's items are
    /// grouped by the package it comes from (<see cref="ToolboxReferences"/>), after the projects', then (#460) those of the
    /// <paramref name="chosen"/> assemblies (Choose Toolbox Items') the project doesn't reference.
    /// </summary>
    public static async Task<WinFormsToolboxResult> ToolboxAsync(Solution solution, string projectPath, CancellationToken cancellationToken, IReadOnlyList<string>? chosen = null)
    {
        var project = solution.Projects.FirstOrDefault(p => p.FilePath is { } path && Paths.Comparer.Equals(path, Path.GetFullPath(projectPath)) && p.Language is LanguageNames.CSharp or LanguageNames.VisualBasic);
        if (project is null)
        {
            return new WinFormsToolboxResult([]);
        }

        var items = new List<WinFormsToolboxItem>();
        var projects = new[] { project }.Concat(project.ProjectReferences.Select(r => solution.GetProject(r.ProjectId)).OfType<Project>()).DistinctBy(p => p.Id);
        Compilation? ownCompilation = null;
        foreach (var candidate in projects)
        {
            if (await candidate.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is not { } compilation)
            {
                continue;
            }

            var own = candidate.Id == project.Id;
            if (own)
            {
                ownCompilation = compilation;
            }

            foreach (var type in Types(compilation.Assembly.GlobalNamespace))
            {
                if (ToolboxItem(type, candidate.Name, internalToo: own) is { } item)
                {
                    items.Add(item);
                }
            }
        }

        var sorted = items.OrderBy(i => i.Project, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase);
        return new WinFormsToolboxResult(
        [
            .. sorted,
            .. ownCompilation is null ? [] : ToolboxReferences.Items(project, ownCompilation, cancellationToken),
            .. ownCompilation is null ? [] : ToolboxReferences.Chosen(ownCompilation, chosen ?? [], cancellationToken),
        ]);
    }

    /// <summary>
    /// <paramref name="type"/> as a Toolbox item of <paramref name="group"/>, or null when the Toolbox doesn't list it: it
    /// must be a public class (or internal in the designed project, <paramref name="internalToo"/>; nested in public
    /// classes), not abstract, generic or static, with a public parameterless constructor, a component that isn't a form,
    /// and neither it nor a base type <c>[ToolboxItem(false)]</c> or <c>[DesignTimeVisible(false)]</c> unless a nearer
    /// type says otherwise (both attributes are inherited).
    /// </summary>
    internal static WinFormsToolboxItem? ToolboxItem(INamedTypeSymbol type, string group, bool internalToo)
    {
        if (type is not { TypeKind: TypeKind.Class, IsAbstract: false, IsGenericType: false, IsStatic: false }
            || !Visible(type, internalToo)
            || !InheritsFrom(type, ComponentType) || InheritsFrom(type, FormType)
            || !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
            || Inherited(type, "ToolboxItemAttribute") is { } toolboxItem && Hides(toolboxItem)
            || Inherited(type, "DesignTimeVisibleAttribute") is { ConstructorArguments: [{ Value: false }] })
        {
            return null;
        }

        var description = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "DescriptionAttribute")?.ConstructorArguments.FirstOrDefault().Value as string;
        var tab = InheritedByName(type, "ToolboxTabNameAttribute")?.ConstructorArguments.FirstOrDefault(a => a.Value is string).Value as string;
        return new WinFormsToolboxItem(SemanticDesignerTypeResolver.Name(type), type.Name, group, InheritsFrom(type, ControlType))
        {
            Description = description,
            Tab = string.IsNullOrWhiteSpace(tab) ? null : tab.Trim(),
        };

        static bool Visible(INamedTypeSymbol type, bool internalToo)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
            {
                if (!(current.DeclaredAccessibility == Accessibility.Public || (internalToo && current.DeclaredAccessibility == Accessibility.Internal)))
                {
                    return false;
                }
            }

            return true;
        }

        // The nearest of the type and its base types that has the attribute, or one derived from it (an inherited one, as
        // TypeDescriptor reads it): a control library marks its controls with its own ToolboxItemAttribute (a DevExpress
        // editor's [DXToolboxItem] over its base's [ToolboxItem(false)]).
        static AttributeData? Inherited(INamedTypeSymbol type, string attribute)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (current.GetAttributes().FirstOrDefault(a => DerivesFrom(a.AttributeClass, attribute)) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        // A control library's own attribute (DevExpress's ToolboxTabNameAttribute), by its name in any namespace.
        static AttributeData? InheritedByName(INamedTypeSymbol type, string attribute)
        {
            for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (current.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == attribute) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        static bool DerivesFrom(INamedTypeSymbol? type, string name)
        {
            for (; type is not null; type = type.BaseType)
            {
                if (type.Name == name && type.ContainingNamespace?.ToDisplayString() == "System.ComponentModel")
                {
                    return true;
                }
            }

            return false;
        }

        // Whether a [ToolboxItem] (or an attribute derived from it) leaves its type out. Its own: given false. A derived
        // one's constructor can't be run from metadata: given false, or a kind named Hidden or None (DevExpress's
        // DXToolboxItemKind.Hidden), as the libraries' constructors pass them on to ToolboxItemAttribute's.
        static bool Hides(AttributeData attribute) => attribute.ConstructorArguments.Any(a => a switch
        {
            { Kind: TypedConstantKind.Primitive, Value: false } => true,
            { Kind: TypedConstantKind.Enum, Type: INamedTypeSymbol enumType } => enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, a.Value))?.Name is "Hidden" or "None",
            _ => false,
        });
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
