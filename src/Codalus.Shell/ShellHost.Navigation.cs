using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell;

/// <summary>
/// Navigation depth (M12a): Go to Base, Peek Definition, Call and Type Hierarchy, the Document Outline, the Code
/// Definition window, F1 help, next and previous problem, and the last edit location.
/// </summary>
public sealed partial class ShellHost
{
    private (string Path, Position Position)? _lastEdit;
    private CodeEditorController? _editTracked;
    private CancellationTokenSource? _codeDefinitionCts;

    public DocumentOutlineViewModel DocumentOutline { get; } = new();

    public HierarchyViewModel Hierarchy { get; private set; } = null!;

    public CodeDefinitionViewModel CodeDefinition { get; private set; } = null!;

    /// <summary>Class View (deferred from M12a): the solution's namespaces, types and members.</summary>
    public SymbolBrowserViewModel ClassView { get; private set; } = null!;

    private bool _classViewShown;
    private UiDebouncer? _symbolBrowsers;

    private void InitializeNavigation()
    {
        Hierarchy = new HierarchyViewModel(OpenLocationWithHistoryAsync);
        CodeDefinition = new CodeDefinitionViewModel(OpenLocationWithHistoryAsync, this, EditorFont);
        ClassView = new SymbolBrowserViewModel(false, () => Languages.RunningClient(CSharpLanguageId), Solutions, OpenLocationWithHistoryAsync);
        Solutions.SolutionChanged += (_, _) => QueueSymbolBrowsers();
        Solutions.ProjectChanged += (_, _) => QueueSymbolBrowsers();
        InitializeAnalysis();
    }

    /// <summary>Class View's content, filled when it is first shown.</summary>
    private SymbolBrowserViewModel ShowClassView()
    {
        if (!_classViewShown)
        {
            _classViewShown = true;
            _ = ClassView.RefreshAsync();
        }

        return ClassView;
    }

    /// <summary>
    /// The projects changed: Class View and an open Object Browser list them again, once a burst settles (M56: each
    /// evaluation of a large solution's open rebuilt Class View's roots, and its flag was set from the build host's thread).
    /// </summary>
    private void QueueSymbolBrowsers() =>
        (_symbolBrowsers ??= new UiDebouncer(RefreshSymbolBrowsers, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3))).Request();

    private void RefreshSymbolBrowsers()
    {
        if (_classViewShown)
        {
            _ = ClassView.RefreshAsync();
        }

        if (Layout?.FindDocument<PageDocument>(SymbolBrowserViewModel.ObjectBrowserId) is { ViewModel: SymbolBrowserViewModel browser })
        {
            _ = browser.RefreshAsync();
        }
    }

    /// <summary>View → Object Browser (deferred from M12a): a project's assemblies, its own first, down to their members.</summary>
    public SymbolBrowserViewModel OpenObjectBrowser()
    {
        if (Layout.FindDocument<PageDocument>(SymbolBrowserViewModel.ObjectBrowserId) is { ViewModel: SymbolBrowserViewModel existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            return existing;
        }

        var viewModel = new SymbolBrowserViewModel(true, () => Languages.RunningClient(CSharpLanguageId), Solutions, OpenLocationWithHistoryAsync);
        Layout.OpenDocument(new PageDocument(SymbolBrowserViewModel.ObjectBrowserId, viewModel.Title, SymbolBrowserViewModel.ObjectBrowserId, viewModel), focus: true);
        _ = viewModel.RefreshAsync();
        return viewModel;
    }

    private Task OpenLocationWithHistoryAsync(Location location)
    {
        RememberLocation();
        return OpenLocationAsync(location);
    }

    private void OnNavigationEditorChanged()
    {
        var controller = ActiveEditor?.Controller;
        DocumentOutline.SetEditor(controller);
        SyntaxVisualizer.SetEditor(controller);
        if (_editTracked != controller)
        {
            if (_editTracked is not null)
            {
                _editTracked.Editor.Document.Changed -= OnEditorTextChanged;
                _editTracked.CaretChanged -= OnCodeDefinitionCaret;
            }

            _editTracked = controller;
            if (controller is not null)
            {
                controller.Editor.Document.Changed += OnEditorTextChanged;
                controller.CaretChanged += OnCodeDefinitionCaret;
                OnCodeDefinitionCaret(controller, EventArgs.Empty);
            }
        }
    }

    // ------------------------------------------------------------------ last edit location

    private void OnEditorTextChanged(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e)
    {
        if (_editTracked is { } controller && !controller.Model.IsReadOnly)
        {
            var edited = Editor.DocumentEdits.EditedSpan(e); // a group's last edit, not the end of a whole-document change
            var offset = Math.Min(edited.Offset + edited.InsertionLength, controller.Model.Text.TextLength);
            _lastEdit = (controller.Model.Path, controller.Model.PositionAt(offset));
        }
    }

    /// <summary>Ctrl+Shift+Backspace: back to where the text was last changed.</summary>
    public async Task GoToLastEditAsync()
    {
        if (_lastEdit is not { } edit)
        {
            ShowMessage("No edits yet.");
            return;
        }

        RememberLocation();
        await OpenFileAsync(edit.Path, preview: false, edit.Position).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ problems

    /// <summary>F8 / Shift+F8: the next or previous error or warning in the document, wrapping around.</summary>
    public void GoToProblem(int direction)
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        var problems = Languages.Diagnostics.For(editor.Model.Path)
            .Where(d => d.Severity <= DiagnosticSeverity.Warning)
            .OrderBy(d => d.Range.Start.Line).ThenBy(d => d.Range.Start.Character)
            .ToList();
        if (problems.Count == 0)
        {
            ShowMessage("No errors or warnings in this document.");
            return;
        }

        var caret = editor.Model.PositionAt(controller.Editor.CaretOffset);
        static int Compare(Position a, Position b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : a.Character.CompareTo(b.Character);
        var target = direction > 0
            ? problems.FirstOrDefault(d => Compare(d.Range.Start, caret) > 0) ?? problems[0]
            : problems.LastOrDefault(d => Compare(d.Range.Start, caret) < 0) ?? problems[^1];
        controller.Navigate(target.Range.Start);
        var index = problems.IndexOf(target) + 1;
        StatusBar.ShowMessage($"{(target.Severity == DiagnosticSeverity.Error ? "Error" : "Warning")} {index} of {problems.Count}: {target.Code} {target.Message.Split('\n')[0]}", TimeSpan.FromSeconds(6));
    }

    // ------------------------------------------------------------------ Go to Base, F1

    public async Task GoToBaseAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client))
        {
            return;
        }

        if (editor.Model.LanguageId != CSharpLanguageId)
        {
            ShowMessage("Go to Base is available for C#.");
            return;
        }

        var locations = await client.GoToBaseAsync(editor.Model.Uri, editor.Model.PositionAt(controller.Editor.CaretOffset), CancellationToken.None).ConfigureAwait(true);
        if (locations.Length == 0)
        {
            ShowMessage("No base types or members found.");
            return;
        }

        OpenLocations(locations, "Go to Base");
    }

    /// <summary>Shift+F1: the API reference of the symbol at the caret (framework symbols), or the package's site.</summary>
    public async Task ShowHelpAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        var word = WordAt(controller.Editor.Document, controller.Editor.CaretOffset);
        string? url = null;
        if (((IEditorServices)this).Client(editor.Model) is { } client && editor.Model.LanguageId == CSharpLanguageId)
        {
            var details = await client.SymbolDetailsAsync(editor.Model.Uri, editor.Model.PositionAt(controller.Editor.CaretOffset), CancellationToken.None).ConfigureAwait(true);
            if (details is { InSource: true })
            {
                ShowMessage($"{details.DisplayName} is defined in this solution.");
                return;
            }

            url = details?.HelpUrl;
        }

        if (url is null && word.Length == 0)
        {
            ShowMessage("Place the caret on a symbol to get help.");
            return;
        }

        url ??= "https://learn.microsoft.com/search/?terms=" + Uri.EscapeDataString(word);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            OpenHelpUrl(uri);
        }
    }

    /// <summary>Opens a help page in the browser (tests replace it).</summary>
    internal Action<Uri> OpenHelpUrl { get; set; } = Codalus.Platform.ShellIntegration.OpenUrl;

    /// <summary>Opens a page or a folder with the operating system's browser or file manager (the command sweep and tests replace it).</summary>
    internal Action<Uri> OpenExternalUrl { get; set; } = Codalus.Platform.ShellIntegration.OpenUrl;

    /// <summary>Shows a file or folder in the operating system's file manager (the command sweep and tests replace it).</summary>
    internal Action<string> RevealPath { get; set; } = Codalus.Platform.ShellIntegration.RevealInFileManager;

    // ------------------------------------------------------------------ Peek Definition, Code Definition window

    /// <summary>The text of a location's document, its title and language: files, decompiled or embedded sources, generated files.</summary>
    internal async Task<(string Title, string Text, string? LanguageId)?> TextOfLocationAsync(string uri, CancellationToken cancellationToken)
    {
        if (Languages.RunningClient(CSharpLanguageId) is { } client && (DocumentUris.IsMetadata(uri) || DocumentUris.IsGenerated(uri)))
        {
            if (DocumentUris.IsMetadata(uri))
            {
                return await client.MetadataTextAsync(uri, cancellationToken).ConfigureAwait(true) is { } metadata
                    ? ($"{metadata.TypeName} — {metadata.AssemblyName} ({MetadataOriginTitle(metadata.Origin)})", metadata.Text, CSharpLanguageId)
                    : null;
            }

            return await client.SourceGeneratedTextAsync(uri, cancellationToken).ConfigureAwait(true) is { } generated
                ? ($"{generated.HintName} — {generated.GeneratorName} (generated)", generated.Text, CSharpLanguageId)
                : null;
        }

        if (DocumentUris.ToPath(uri) is not { } path)
        {
            return null;
        }

        var text = OpenTextOf(path) ?? (File.Exists(path) ? (await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true)).Text : null);
        return text is null ? null : (Path.GetFileName(path), text, Languages.LanguageIdOf(path));
    }

    internal static string MetadataOriginTitle(string? origin) => origin switch
    {
        MetadataOrigins.Embedded => "embedded source",
        MetadataOrigins.SourceLink => "Source Link",
        _ => "decompiled",
    };

    /// <summary>Alt+F12: the definition in a popup under the caret's line.</summary>
    public async Task PeekDefinitionAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client))
        {
            return;
        }

        var locations = await client.DefinitionAsync(editor.Model.Uri, editor.Model.PositionAt(controller.Editor.CaretOffset), CancellationToken.None).ConfigureAwait(true);
        if (locations.Length == 0)
        {
            ShowMessage("Cannot navigate to the symbol under the caret.");
            return;
        }

        var location = locations[0];
        if (await TextOfLocationAsync(location.Uri, CancellationToken.None).ConfigureAwait(true) is not var (title, text, languageId))
        {
            ShowMessage("The definition's source is not available.");
            return;
        }

        editor.View?.ShowPeek(location, $"{title} ({location.Range.Start.Line + 1})", text, languageId);
    }

    private void OnCodeDefinitionCaret(object? sender, EventArgs e)
    {
        if (!Layout.IsVisible(CodeDefinitionViewModel.ToolWindowId))
        {
            return;
        }

        var token = RestartCts(ref _codeDefinitionCts).Token;
        Avalonia.Threading.DispatcherTimer.RunOnce(() => _ = UpdateCodeDefinitionAsync(token), TimeSpan.FromMilliseconds(350), Avalonia.Threading.DispatcherPriority.Background);
    }

    private static CancellationTokenSource RestartCts(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts;
    }

    /// <summary>Shows the definition at the active editor's caret in the Code Definition window.</summary>
    public async Task UpdateCodeDefinitionAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || ActiveEditor is not { Controller: { } controller } editor
            || ((IEditorServices)this).Client(editor.Model) is not { Capabilities.DefinitionProvider: true } client)
        {
            return;
        }

        try
        {
            var locations = await client.DefinitionAsync(editor.Model.Uri, editor.Model.PositionAt(controller.Editor.CaretOffset), cancellationToken).ConfigureAwait(true);
            if (locations.Length == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (await TextOfLocationAsync(locations[0].Uri, cancellationToken).ConfigureAwait(true) is var (title, text, languageId) && !cancellationToken.IsCancellationRequested)
            {
                CodeDefinition.Show(locations[0], $"{title} ({locations[0].Range.Start.Line + 1})", text, languageId);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException or InvalidOperationException)
        {
        }
    }

    // ------------------------------------------------------------------ hierarchies

    /// <summary>Shift+Alt+H: callers and callees of the method or property at the caret.</summary>
    public async Task ShowCallHierarchyAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client))
        {
            return;
        }

        if (client.Capabilities?.CallHierarchyProvider != true)
        {
            ShowMessage("This file's language service has no call hierarchy.");
            return;
        }

        var items = await client.PrepareCallHierarchyAsync(editor.Model.Uri, editor.Model.PositionAt(controller.Editor.CaretOffset), CancellationToken.None).ConfigureAwait(true);
        if (items.Length == 0)
        {
            ShowMessage("Place the caret on a method, property, event or field.");
            return;
        }

        Hierarchy.Show($"Call Hierarchy: {items[0].Name}", items.Select(i => CallRoot(client, i)));
        Layout.ShowToolWindow(HierarchyViewModel.ToolWindowId);
        foreach (var root in Hierarchy.Roots)
        {
            root.IsExpanded = true;
        }
    }

    private static Location? DeclarationOf(string uri, LanguageServices.Protocol.Range range) => uri.Length == 0 ? null : new Location(uri, range);

    private static HierarchyNodeViewModel CallRoot(LanguageHostClient client, CallHierarchyItem item) =>
        new(item.Name, item.Detail, "method", DeclarationOf(item.Uri, item.SelectionRange), () => Task.FromResult<IReadOnlyList<HierarchyNodeViewModel>>(
        [
            new HierarchyNodeViewModel($"Calls To '{item.Name}'", null, "arrow-down", null, () => IncomingAsync(client, item)),
            new HierarchyNodeViewModel($"Calls From '{item.Name}'", null, "arrow-down", null, () => OutgoingAsync(client, item)),
        ]));

    private static async Task<IReadOnlyList<HierarchyNodeViewModel>> IncomingAsync(LanguageHostClient client, CallHierarchyItem item)
    {
        var calls = await client.IncomingCallsAsync(item, CancellationToken.None).ConfigureAwait(true);
        return [.. calls.Select(c => new HierarchyNodeViewModel(
            c.From.Name,
            $"{c.From.Detail} · {(c.FromRanges.Length == 1 ? "1 call" : $"{c.FromRanges.Length} calls")}",
            "method",
            c.FromRanges.Length > 0 ? new Location(c.From.Uri, c.FromRanges[0]) : DeclarationOf(c.From.Uri, c.From.SelectionRange),
            () => IncomingAsync(client, c.From)))];
    }

    private static async Task<IReadOnlyList<HierarchyNodeViewModel>> OutgoingAsync(LanguageHostClient client, CallHierarchyItem item)
    {
        var calls = await client.OutgoingCallsAsync(item, CancellationToken.None).ConfigureAwait(true);
        return [.. calls.Select(c => new HierarchyNodeViewModel(
            c.To.Name,
            c.To.Detail,
            "method",
            DeclarationOf(c.To.Uri, c.To.SelectionRange),
            c.To.Uri.Length == 0 ? null : () => OutgoingAsync(client, c.To)))];
    }

    /// <summary>Base and derived types of the type at the caret.</summary>
    public async Task ShowTypeHierarchyAsync()
    {
        if (ActiveLanguageEditor() is not var (editor, controller, client))
        {
            return;
        }

        if (client.Capabilities?.TypeHierarchyProvider != true)
        {
            ShowMessage("This file's language service has no type hierarchy.");
            return;
        }

        var items = await client.PrepareTypeHierarchyAsync(editor.Model.Uri, editor.Model.PositionAt(controller.Editor.CaretOffset), CancellationToken.None).ConfigureAwait(true);
        if (items.Length == 0)
        {
            ShowMessage("Place the caret on a type.");
            return;
        }

        Hierarchy.Show($"Type Hierarchy: {items[0].Name}", items.Select(i => new HierarchyNodeViewModel(i.Name, i.Detail, "class", DeclarationOf(i.Uri, i.SelectionRange), () => Task.FromResult<IReadOnlyList<HierarchyNodeViewModel>>(
        [
            new HierarchyNodeViewModel("Base Types", null, "arrow-down", null, () => TypesAsync(client, i, supertypes: true)),
            new HierarchyNodeViewModel("Derived Types", null, "arrow-down", null, () => TypesAsync(client, i, supertypes: false)),
        ]))));
        Layout.ShowToolWindow(HierarchyViewModel.ToolWindowId);
        foreach (var root in Hierarchy.Roots)
        {
            root.IsExpanded = true;
        }
    }

    private static async Task<IReadOnlyList<HierarchyNodeViewModel>> TypesAsync(LanguageHostClient client, TypeHierarchyItem item, bool supertypes)
    {
        var types = supertypes
            ? await client.SupertypesAsync(item, CancellationToken.None).ConfigureAwait(true)
            : await client.SubtypesAsync(item, CancellationToken.None).ConfigureAwait(true);
        return [.. types.Select(t => new HierarchyNodeViewModel(t.Name, t.Detail, "class", DeclarationOf(t.Uri, t.SelectionRange), () => TypesAsync(client, t, supertypes)))];
    }
}
