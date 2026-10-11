using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.ExternalTools;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Shell;

/// <summary>
/// Project-system depth (M26a, ADR 0033): the Properties window, Show All Files with Include and Exclude, Add New and Add
/// Existing Item (and links), the template manager, Export Template, and External Tools.
/// </summary>
public sealed partial class ShellHost
{
    private static readonly ContributionSource s_externalToolsSource = new("daedalus.externalTools", IsBuiltIn: true);

    private readonly List<IDisposable> _externalToolCommands = [];

    public ProjectItemService ProjectItems { get; private set; } = null!;

    public TemplateService Templates { get; private set; } = null!;

    public PropertiesWindowViewModel PropertiesWindow { get; private set; } = null!;

    /// <summary>Where Export Template writes templates (one folder per short name).</summary>
    public string TemplatesFolder => Path.Combine(Paths.Data, "templates");

    public string ExternalToolsFile => Path.Combine(Paths.Config, ExternalToolStore.FileName);

    public IReadOnlyList<ExternalTool> ExternalTools { get; private set; } = [];

    private void InitializeProjectSystem()
    {
        ProjectItems = new ProjectItemService(Solutions);
        Templates = new TemplateService(workingDirectory: Paths.State);
        PropertiesWindow = new PropertiesWindowViewModel(this);
        Solutions.FilesChanged += (_, directories) => Ui.Post(() => Explorer.OnFilesChanged(directories));
        Builds.Finished += (_, _) => Ui.Post(Explorer.RefreshDisk);
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            if (!Solutions.IsOpen)
            {
                foreach (var document in Layout.Documents.OfType<SolutionManagementDocument>().Where(d => d.ViewModel is AddNewItemViewModel or ExportTemplateViewModel).ToList())
                {
                    Layout.CloseDocument(document);
                }
            }
        });
        LoadExternalTools();
    }

    // ------------------------------------------------------------------ the selection

    /// <summary>The project and folder new items go into: the selected folder, project or file's folder.</summary>
    private (string Project, string Folder)? ItemTarget(ExplorerNode? node = null)
    {
        node ??= Explorer.Selected?.Node;
        return node switch
        {
            { Kind: ExplorerNodeKind.Folder, Path: { } folder, ProjectPath: { } project } => (project, folder),
            { Kind: ExplorerNodeKind.Project, Path: { } project } => (project, Path.GetDirectoryName(project)!),
            { Kind: ExplorerNodeKind.File, Path: { } file, ProjectPath: { } project, IsLinked: false } => (project, Path.GetDirectoryName(file)!),
            { ProjectPath: { } project } => (project, Path.GetDirectoryName(project)!),
            _ => CurrentProject() is { } current ? (current, Path.GetDirectoryName(current)!) : null,
        };
    }

    // ------------------------------------------------------------------ include and exclude

    /// <summary>Exclude From Project for the selected file or folder (a linked file loses its link).</summary>
    public async Task ExcludeFromProjectAsync()
    {
        if (Explorer.Selected?.Node is not { Kind: ExplorerNodeKind.File or ExplorerNodeKind.Folder, Path: { } path, ProjectPath: { } project, IsExcluded: false })
        {
            return;
        }

        await RunItemEditAsync(() => ProjectItems.ExcludeAsync(project, [path], CancellationToken.None)).ConfigureAwait(true);
    }

    /// <summary>Include In Project for the selected (excluded) file or folder.</summary>
    public Task IncludeSelectedInProjectAsync() =>
        Explorer.Selected?.Node is { Path: { } path, ProjectPath: { } project, IsExcluded: true } ? IncludeInProjectAsync(project, [path]) : Task.CompletedTask;

    public Task IncludeInProjectAsync(string project, IReadOnlyList<string> paths) =>
        RunItemEditAsync(() => ProjectItems.IncludeAsync(project, paths, CancellationToken.None));

    private async Task RunItemEditAsync(Func<Task> edit)
    {
        if (!RequireTrustedSolution("Project item edits"))
        {
            return;
        }

        try
        {
            await edit().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or IOException or OperationCanceledException)
        {
            Workbench.Notifications.Error(ex.Message);
        }
    }

    // ------------------------------------------------------------------ add items

    /// <summary>Add Existing Item (or, with <paramref name="asLink"/>, Add As Link) into the selected folder.</summary>
    public async Task AddExistingItemAsync(bool asLink, IReadOnlyList<string>? files = null)
    {
        if (!RequireTrustedSolution("Adding items") || ItemTarget() is not { } target)
        {
            return;
        }

        files ??= Window is null ? [] : await Window.PickFilesAsync(asLink ? "Add Existing Item As Link" : "Add Existing Item").ConfigureAwait(true);
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            var result = await ProjectItems.AddExistingAsync(target.Project, target.Folder, files, asLink, CancellationToken.None).ConfigureAwait(true);
            var first = result.CreatedPaths.FirstOrDefault() ?? files[0];
            Explorer.RevealWhenAvailable("f:" + first);
        }
        catch (Exception ex) when (ex is JsonRpcRemoteException or InvalidOperationException or JsonRpcConnectionLostException or IOException)
        {
            Workbench.Notifications.Error(ex.Message);
        }
    }

    /// <summary>Add New Item (Ctrl+Shift+A): the template document for the selected folder of a project.</summary>
    public AddNewItemViewModel? OpenAddNewItem(string? project = null, string? folder = null)
    {
        if (!Solutions.IsOpen)
        {
            StatusBar.ShowMessage("Open a solution or a folder with a project to add items.", TimeSpan.FromSeconds(4));
            return null;
        }

        var target = project is not null ? (Path.GetFullPath(project), folder is null ? Path.GetDirectoryName(Path.GetFullPath(project))! : Path.GetFullPath(folder)) : ItemTarget();
        if (target is not { } chosen)
        {
            StatusBar.ShowMessage("Select a project or one of its folders to add an item to.", TimeSpan.FromSeconds(4));
            return null;
        }

        foreach (var open in Layout.Documents.OfType<SolutionManagementDocument>().Where(d => d.ViewModel is AddNewItemViewModel).ToList())
        {
            Layout.CloseDocument(open);
        }

        var viewModel = new AddNewItemViewModel(this, chosen.Item1, chosen.Item2);
        var document = new SolutionManagementDocument("addNewItem", "Add New Item", viewModel);
        viewModel.Closed += (_, _) => Ui.Post(() => Layout.CloseDocument(document));
        Layout.OpenDocument(document, focus: true);
        return viewModel;
    }

    // ------------------------------------------------------------------ templates

    public TemplateManagerViewModel OpenTemplateManager()
    {
        if (Layout.FindDocument<PageDocument>("templates") is { ViewModel: TemplateManagerViewModel existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            return existing;
        }

        var viewModel = new TemplateManagerViewModel(Templates, Dialogs);
        Layout.OpenDocument(new PageDocument("templates", "Templates", "templates", viewModel), focus: true);
        return viewModel;
    }

    /// <summary>Export Template: the selected project (or file, as an item template).</summary>
    public ExportTemplateViewModel? OpenExportTemplate()
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage("Open a solution to export a project or an item as a template.", TimeSpan.FromSeconds(4));
            return null;
        }

        foreach (var open in Layout.Documents.OfType<SolutionManagementDocument>().Where(d => d.ViewModel is ExportTemplateViewModel).ToList())
        {
            Layout.CloseDocument(open);
        }

        var node = Explorer.Selected?.Node;
        var item = node is { Kind: ExplorerNodeKind.File, Path: { } file, IsExcluded: false } && !SolutionTree.IsProjectFile(file) ? file : null;
        var viewModel = new ExportTemplateViewModel(Templates, TemplatesFolder, [.. solution.Projects.Select(p => new SolutionProjectChoice(p.Name, p.Path))], node?.ProjectPath ?? CurrentProject(), item);
        Layout.OpenDocument(new SolutionManagementDocument("exportTemplate", "Export Template", viewModel), focus: true);
        return viewModel;
    }

    // ------------------------------------------------------------------ External Tools

    private void LoadExternalTools()
    {
        ExternalTools = ExternalToolStore.Load(ExternalToolsFile);
        RegisterExternalTools();
    }

    /// <summary>One command per tool (<c>externalTools.run.&lt;id&gt;</c>), in the Tools menu's External Tools group.</summary>
    private void RegisterExternalTools()
    {
        foreach (var registration in _externalToolCommands)
        {
            registration.Dispose();
        }

        _externalToolCommands.Clear();
        Workbench.Menus.RemoveFrom(s_externalToolsSource);
        var order = 0;
        foreach (var tool in ExternalTools)
        {
            var captured = tool;
            _externalToolCommands.Add(Workbench.Commands.Register(
                new CommandDescriptor(tool.CommandId, tool.Title, s_externalToolsSource) { Category = "External Tools" },
                async (_, _) =>
                {
                    await Ui.RunAsync(() => RunExternalToolAsync(captured)).ConfigureAwait(false);
                    return null;
                }));
            Workbench.Menus.Add([new MenuItemRule(MenuLocations.MenuBar("tools"), tool.CommandId, s_externalToolsSource) { Group = "external", Order = order++ }]);
        }
    }

    /// <summary>Saves the tools and updates their commands and menu entries.</summary>
    public Task SaveExternalToolsAsync(IReadOnlyList<ExternalTool> tools)
    {
        ExternalToolStore.Save(ExternalToolsFile, tools);
        ExternalTools = tools;
        RegisterExternalTools();
        return Task.CompletedTask;
    }

    public ExternalToolsViewModel OpenExternalTools()
    {
        if (Layout.FindDocument<PageDocument>("externalTools") is { ViewModel: ExternalToolsViewModel existing } open)
        {
            Layout.OpenDocument(open, focus: true);
            return existing;
        }

        var viewModel = new ExternalToolsViewModel(() => ExternalToolStore.Load(ExternalToolsFile), SaveExternalToolsAsync, RunExternalToolAsync, ToolMacros);
        Layout.OpenDocument(new PageDocument("externalTools", "External Tools", "externalTools", viewModel), focus: true);
        return viewModel;
    }

    /// <summary>Runs a tool: its macros expanded, the arguments shown first when it asks, its output in the Output window when it uses it.</summary>
    public async Task RunExternalToolAsync(ExternalTool tool)
    {
        var values = await ToolMacroValuesAsync(tool).ConfigureAwait(true);
        string Expand(string text) => ExternalToolStore.Expand(text, name => values.GetValueOrDefault(name));
        var arguments = Expand(tool.Arguments);
        if (tool.PromptForArguments)
        {
            if (await Dialogs.InputAsync(tool.Title, $"Arguments for {tool.Command}:", arguments).ConfigureAwait(true) is not { } edited)
            {
                return;
            }

            arguments = edited;
        }

        var channel = tool.UseOutputWindow ? Workbench.Output.GetOrCreate("daedalus.externalTools", "External Tools") : null;
        if (channel is not null)
        {
            Workbench.Output.Show(channel);
        }

        try
        {
            await ExternalToolRunner.RunAsync(Expand(tool.Command), arguments, Expand(tool.InitialDirectory), channel, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Workbench.Notifications.Error($"Could not run {tool.Title}: {ex.Message}");
        }
    }

    /// <summary>The values of the macros a tool uses: from the active editor, the Solution Explorer's selection, its project and the solution.</summary>
    private async Task<Dictionary<string, string>> ToolMacroValuesAsync(ExternalTool tool)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        static string Dir(string path) => Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        var item = Equals(Workbench.Context.GetValue("focusedView"), SolutionExplorerViewModel.ToolWindowId) && Explorer.Selected?.Node.Path is { } selected && File.Exists(selected)
            ? selected
            : ActiveEditor?.Model.Path ?? Explorer.Selected?.Node.Path;
        if (item is not null)
        {
            values["ItemPath"] = item;
            values["ItemDir"] = Dir(Path.GetDirectoryName(item) ?? item);
            values["ItemFileName"] = Path.GetFileNameWithoutExtension(item);
            values["ItemExt"] = Path.GetExtension(item);
        }

        if (CurrentProject() is { } project)
        {
            values["ProjectPath"] = project;
            values["ProjectDir"] = Dir(Path.GetDirectoryName(project)!);
            values["ProjectFileName"] = Path.GetFileName(project);
            values["BinDir"] = Dir(Path.Combine(Path.GetDirectoryName(project)!, "bin"));
            if (Solutions.CompilerInputs.GetValueOrDefault(project)?.OutputPath is { } target)
            {
                var full = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, target));
                values["TargetPath"] = full;
                values["TargetDir"] = Dir(Path.GetDirectoryName(full)!);
                values["TargetName"] = Path.GetFileNameWithoutExtension(full);
                values["TargetExt"] = Path.GetExtension(full);
            }
        }

        if (Solutions.Solution is { } solution)
        {
            values["SolutionDir"] = Dir(solution.Directory);
            values["SolutionFileName"] = solution.IsFolder ? string.Empty : Path.GetFileName(solution.Path);
        }

        if (ActiveEditor?.Controller?.Editor is { } editor)
        {
            values["CurLine"] = editor.TextArea.Caret.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["CurCol"] = editor.TextArea.Caret.Column.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["SelText"] = editor.SelectedText;
            values["CurText"] = ToolWordAt(editor.Document.Text, editor.CaretOffset);
        }

        foreach (var name in ExternalToolStore.MacrosIn(tool.Command + " " + tool.Arguments + " " + tool.InitialDirectory).Where(n => !values.ContainsKey(n)))
        {
            if (await PluginMacroAsync(name).ConfigureAwait(true) is { } value)
            {
                values[name] = value;
            }
        }

        return values;
    }

    // ------------------------------------------------------------------ plugin contributions (API 0.2.8)

    /// <summary>
    /// The Properties window's rows from plugins (<c>contributes.propertyProviders</c>): each provider's command gets
    /// <c>{ kind, path, project }</c> and returns <c>[{ category, name, value, description }]</c>.
    /// </summary>
    internal async Task<List<PropertyCategoryViewModel>> PluginPropertyRowsAsync(ExplorerNode node, CancellationToken cancellationToken)
    {
        var rows = new List<(string Category, PropertyRowViewModel Row)>();
        foreach (var (pluginId, provider) in Plugins.PropertyProviders)
        {
            if (provider.When is { Length: > 0 } when && (!DaedalusStudio.Workbench.Context.WhenClause.TryParse(when, out var clause, out _) || !clause.Evaluate(Workbench.Context)))
            {
                continue;
            }

            try
            {
                var argument = JsonArgument(writer =>
                {
                    writer.WriteString("kind", SolutionExplorerViewModel.KindKey(node.Kind));
                    if (node.Path is { } path)
                    {
                        writer.WriteString("path", path);
                    }

                    if (node.ProjectPath is { } project)
                    {
                        writer.WriteString("project", project);
                    }
                });
                var result = await Workbench.Commands.ExecuteAsync(provider.Command, [argument], cancellationToken).ConfigureAwait(true);
                if (result is not { ValueKind: System.Text.Json.JsonValueKind.Array } array)
                {
                    continue;
                }

                var category = Plugins.Plugins.FirstOrDefault(p => p.Id == pluginId)?.DisplayName ?? pluginId;
                foreach (var row in array.EnumerateArray().Where(r => r.ValueKind == System.Text.Json.JsonValueKind.Object))
                {
                    string? Text(string name) => row.TryGetProperty(name, out var value) ? value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.ToString() : null;
                    if (Text("name") is { Length: > 0 } name)
                    {
                        rows.Add((Text("category") ?? category, new PropertyRowViewModel(name, Text("value") ?? string.Empty, PropertyRowKind.ReadOnly, Text("description") ?? string.Empty)));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Workbench.Log.AppendLine($"[properties] {pluginId}'s provider {provider.Command} failed: {ex.Message}");
            }
        }

        return [.. rows.GroupBy(r => r.Category, StringComparer.Ordinal).Select(g => new PropertyCategoryViewModel(g.Key, [.. g.Select(r => r.Row)]))];
    }

    /// <summary>A plugin's External Tools macro (<c>contributes.externalToolMacros</c>): its command's string result.</summary>
    private async Task<string?> PluginMacroAsync(string name)
    {
        if (Plugins.ToolMacros.FirstOrDefault(m => m.Macro.Name.Equals(name, StringComparison.Ordinal)) is not { Macro: { } macro } contribution)
        {
            return null;
        }

        try
        {
            var result = await Workbench.Commands.ExecuteAsync(macro.Command, CancellationToken.None).ConfigureAwait(true);
            return result is { ValueKind: System.Text.Json.JsonValueKind.String } text ? text.GetString() : result?.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Workbench.Log.AppendLine($"[externalTools] {contribution.PluginId}'s macro $({name}) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>The macros External Tools offer: the shell's and the plugins'.</summary>
    public IReadOnlyList<ToolMacro> ToolMacros =>
        [.. ExternalToolStore.Macros, .. Plugins.ToolMacros.Select(m => new ToolMacro(m.Macro.Name, m.Macro.Description ?? $"From the plugin {m.PluginId}."))];

    private static System.Text.Json.JsonElement JsonArgument(Action<System.Text.Json.Utf8JsonWriter> write)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using var document = System.Text.Json.JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static string ToolWordAt(string text, int offset)
    {
        var start = Math.Clamp(offset, 0, text.Length);
        var end = start;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }

        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return text[start..end];
    }
}
