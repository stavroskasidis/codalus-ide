using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Web;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Scaffolding and service references (M15c, ADR 0041): Add New Scaffolded Item, and a project's OpenAPI and gRPC
/// references. The work runs in the build host (the user's dotnet-aspnet-codegenerator, the SDK's item templates,
/// project edits); this is the UI.
/// </summary>
public sealed partial class ShellHost
{
    public ScaffoldingService Scaffolding { get; private set; } = null!;

    public ServiceReferenceService ServiceReferences { get; private set; } = null!;

    private void InitializeWebTools()
    {
        Scaffolding = new ScaffoldingService(Solutions, ProjectManagement, Workbench.Output);
        ServiceReferences = new ServiceReferenceService(Solutions, Workbench.Output);
    }

    /// <summary>Add New Scaffolded Item, for the selected project (or folder), or <paramref name="project"/>.</summary>
    public ScaffoldViewModel? OpenAddScaffoldedItem(string? project = null, string? folder = null)
    {
        if (!RequireTrustedSolution("Scaffolding"))
        {
            return null;
        }

        var target = project is not null ? (Path.GetFullPath(project), folder is null ? null : Path.GetFullPath(folder)) : ItemTarget() is { } selected ? (selected.Project, selected.Folder) : ((string, string?)?)null;
        if (target is not { } chosen)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.webTools.selectAProjectOrOneOf", "Select a project or one of its folders to scaffold into."), TimeSpan.FromSeconds(4));
            return null;
        }

        foreach (var open in Layout.Documents.OfType<SolutionManagementDocument>().Where(d => d.ViewModel is ScaffoldViewModel).ToList())
        {
            Layout.CloseDocument(open);
        }

        var viewModel = new ScaffoldViewModel(chosen.Item1, chosen.Item2, RunScaffolderAsync, InstallScaffoldingAsync);
        Layout.OpenDocument(new SolutionManagementDocument("scaffold", "Add New Scaffolded Item", viewModel), focus: true);
        _ = LoadScaffoldStateAsync(viewModel);
        return viewModel;
    }

    /// <summary>The code generator's state, and the models and contexts Roslyn knows, for the form.</summary>
    private async Task LoadScaffoldStateAsync(ScaffoldViewModel form)
    {
        // The code generator's state first: the form needs it; the suggestions only help.
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            form.Status = await Scaffolding.StatusAsync(form.Project, limit.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            form.Error = ex is OperationCanceledException ? "dotnet-aspnet-codegenerator didn't answer within 2 minutes; see the Scaffolding output." : ex.Message;
        }

        try
        {
            // Roslyn's contexts, while the C# workspace is loading too, but not for long.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var contexts = await EntityFramework.ContextsAsync(fromTool: false, limit.Token).WaitAsync(limit.Token).ConfigureAwait(true);
            var mine = contexts.Where(c => SolutionService.PathComparer.Equals(c.Project, form.Project)).ToList();
            var offered = mine.Count > 0 ? mine : [.. contexts];
            form.SetSuggestions([.. offered.SelectMany(c => c.Entities)], [.. offered.Select(c => c.Name)]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            // No language service yet: the fields take typed names.
        }
    }
    private async Task RunScaffolderAsync(ScaffoldViewModel form)
    {
        if (form.Scaffolder is not { } scaffolder)
        {
            return;
        }

        try
        {
            var result = await Scaffolding.RunAsync(form.Project, scaffolder, form.Values(), CancellationToken.None).ConfigureAwait(true);
            var created = result.Created.Count;
            form.Result = (created == 1 ? "Added 1 file" : $"Added {created} files")
                          + (result.Modified.Count > 0 ? $" and changed {string.Join(", ", result.Modified.Select(Path.GetFileName))}." : ".");
            var open = result.Created.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.EndsWith(".cshtml.cs", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault() ?? (result.Created.Count > 0 ? result.Created[0] : null);
            if (open is not null)
            {
                await OpenFileAsync(open, preview: false).ConfigureAwait(true);
            }
        }
        catch (ScaffoldToolMissingException ex)
        {
            form.Status = ex.Status;
            form.Error = ex.Message + " Use Install to add it.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or OperationCanceledException)
        {
            form.Error = ex.Message;
            Scaffolding.Channel.AppendLine("error: " + ex.Message);
        }
    }

    private async Task InstallScaffoldingAsync(ScaffoldViewModel form)
    {
        var status = form.Status;
        var what = status is { ToolAvailable: false, ManifestListsTool: true }
            ? "Restore the .NET tools (dotnet tool restore)"
            : $"Install dotnet-aspnet-codegenerator {status?.DotnetVersion ?? string.Empty} as a local tool of this solution, and add the packages the code generator needs to {Path.GetFileNameWithoutExtension(form.Project)}";
        if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.webTools.installTheCodeGenerator", "Install the code generator"), what + "? They are downloaded from your NuGet sources.", Localizer.Get("msg.webTools.install", "Install")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            form.Status = await Scaffolding.InstallAsync(form.Project, CancellationToken.None).ConfigureAwait(true);
            form.Result = form.NeedsTool ? null : "The code generator is ready.";
            if (form.NeedsTool)
            {
                form.Error = form.Status.Message ?? "The code generator still can't run; see the Scaffolding output.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            form.Error = ex.Message;
        }
    }

    // ------------------------------------------------------------------ service references

    /// <summary>The service references of the selected project (or <paramref name="project"/>).</summary>
    public ServiceReferencesViewModel? OpenServiceReferences(string? project = null)
    {
        if (!RequireTrustedSolution("Service references"))
        {
            return null;
        }

        if ((project is not null ? Path.GetFullPath(project) : CurrentProject()) is not { } chosen)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.webTools.selectAProjectToManageIts", "Select a project to manage its service references."), TimeSpan.FromSeconds(4));
            return null;
        }

        var id = "serviceReferences:" + chosen;
        if (Layout.FindDocument<SolutionManagementDocument>(id) is { ViewModel: ServiceReferencesViewModel existing } document)
        {
            Layout.OpenDocument(document, focus: true);
            return existing;
        }

        var viewModel = new ServiceReferencesViewModel(chosen, ServiceReferenceActionAsync);
        viewModel.WcfName = ServiceReferenceService.SuggestWcfName(chosen, []);
        Layout.OpenDocument(new SolutionManagementDocument(id, $"Service References ({Path.GetFileNameWithoutExtension(chosen)})", viewModel), focus: true);
        _ = viewModel.RunAsync("reload");
        return viewModel;
    }

    private async Task ServiceReferenceActionAsync(ServiceReferencesViewModel form, string action)
    {
        try
        {
            var selected = form.Selected?.Info;
            switch (action)
            {
                case "reload":
                    break;
                case "browse" when form.IsWcf:
                    if (Window is not null && await Window.PickFileAsync("Choose a WSDL document", "WSDL documents", ["*.wsdl", "*.xml"]).ConfigureAwait(true) is { } wsdl)
                    {
                        form.Source = wsdl;
                    }

                    return;
                case "browse":
                    if (Window is not null && await Window.PickFileAsync(
                            form.IsOpenApi ? "Choose an OpenAPI document" : "Choose a .proto file",
                            form.IsOpenApi ? "OpenAPI documents" : "Protocol Buffers",
                            form.IsOpenApi ? ["*.json", "*.yaml", "*.yml"] : ["*.proto"]).ConfigureAwait(true) is { } picked)
                    {
                        form.Source = picked;
                    }

                    return;
                case "add" when form.IsWcf:
                {
                    if (!await EnsureSvcutilAsync(form.Project).ConfigureAwait(true))
                    {
                        return;
                    }

                    var request = form.WcfRequest();
                    var generated = await ServiceReferences.AddWcfAsync(request, CancellationToken.None).ConfigureAwait(true);
                    form.Result = $"Generated {Path.GetRelativePath(Path.GetDirectoryName(form.Project)!, Path.Combine(generated.Folder, "Reference.cs"))} with dotnet-svcutil"
                                  + (generated.Modified.Any(f => PathComparer.Equals(f, form.Project)) ? ", and added the System.ServiceModel packages." : ".");
                    form.Source = string.Empty;
                    form.Namespace = string.Empty;
                    var listed = await ServiceReferences.ListAsync(form.Project, CancellationToken.None).ConfigureAwait(true);
                    form.SetReferences(listed);
                    form.WcfName = ServiceReferenceService.SuggestWcfName(form.Project, listed.References);
                    return;
                }

                case "add":
                {
                    var (file, edit) = await ServiceReferences.AddAsync(form.Request(), CancellationToken.None).ConfigureAwait(true);
                    form.Result = $"Added {Path.GetFileName(file)}"
                                  + (edit.AddedPackages.Length > 0 ? $" and the packages {string.Join(", ", edit.AddedPackages)}" : string.Empty)
                                  + ". The code is generated when the project builds.";
                    form.Source = string.Empty;
                    break;
                }

                case "save" when selected is { Kind: ServiceReferenceKinds.Wcf } && form.WcfEdit() is { } wcfEdit:
                    await ServiceReferences.UpdateWcfAsync(wcfEdit, CancellationToken.None).ConfigureAwait(true);
                    form.Result = $"Generated {selected.ClassName} again with the new options.";
                    break;
                case "save" when selected is not null:
                    await ServiceReferences.UpdateAsync(
                        new ServiceReferenceParams(form.Project, selected.Kind, selected.Path, selected.SourceUrl,
                            Empty(form.EditNamespace), Empty(form.EditClassName), selected.CodeGenerator, Empty(form.EditOptions),
                            selected.Kind == ServiceReferenceKinds.Grpc ? form.EditServices : null, selected.Access),
                        CancellationToken.None).ConfigureAwait(true);
                    form.Result = $"Saved {Path.GetFileName(selected.Path)}.";
                    break;
                case "refresh" when selected is not null:
                    await ServiceReferences.RefreshAsync(form.Project, selected, CancellationToken.None).ConfigureAwait(true);
                    form.Result = selected.Kind == ServiceReferenceKinds.Wcf
                        ? $"Generated {selected.ClassName} again from {selected.Input}."
                        : $"Downloaded {Path.GetFileName(selected.Path)} again from {selected.SourceUrl}.";
                    break;
                case "open" when selected is { Kind: ServiceReferenceKinds.Wcf }:
                    await OpenFileAsync(Path.Combine(Path.GetDirectoryName(selected.Path)!, "Reference.cs"), preview: false).ConfigureAwait(true);
                    return;
                case "test" when selected is { Kind: ServiceReferenceKinds.Wcf }:
                    OpenWcfTestClient(selected.Input);
                    return;
                case "open" when selected is not null:
                    await OpenFileAsync(selected.Path, preview: false).ConfigureAwait(true);
                    return;
                case "remove" when selected is { Kind: ServiceReferenceKinds.Wcf }:
                {
                    if (!await Dialogs.ConfirmAsync(
                            Localizer.Get("msg.webTools.removeTheServiceReference", "Remove the service reference"),
                            Localizer.Format("msg.webTools.removeTheWcfReference", "Remove {0} from {1}? Its folder of generated code goes to the trash; the System.ServiceModel packages stay.", selected.ClassName, Path.GetFileNameWithoutExtension(form.Project)),
                            Localizer.Get("msg.webTools.remove", "Remove")).ConfigureAwait(true))
                    {
                        return;
                    }

                    await ServiceReferences.RemoveAsync(form.Project, selected, deleteFile: true, CancellationToken.None).ConfigureAwait(true);
                    form.Result = $"Removed {selected.ClassName}.";
                    break;
                }

                case "remove" when selected is not null:
                {
                    if (!await Dialogs.ConfirmAsync(
                            Localizer.Get("msg.webTools.removeTheServiceReference", "Remove the service reference"),
                            Localizer.Format("msg.webTools.removeTheReferenceToFromThe", "Remove the reference to {0} from {1}? The document and the packages stay; delete them yourself if nothing else uses them.", Path.GetFileName(selected.Path), Path.GetFileNameWithoutExtension(form.Project)),
                            Localizer.Get("msg.webTools.remove", "Remove")).ConfigureAwait(true))
                    {
                        return;
                    }

                    await ServiceReferences.RemoveAsync(form.Project, selected, deleteFile: false, CancellationToken.None).ConfigureAwait(true);
                    form.Result = $"Removed {Path.GetFileName(selected.Path)}.";
                    break;
                }

                default:
                    return;
            }

            form.SetReferences(await ServiceReferences.ListAsync(form.Project, CancellationToken.None).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException or HttpRequestException or TaskCanceledException)
        {
            form.Error = ex.Message;
        }
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Makes sure dotnet-svcutil runs for the project (M42): when it doesn't, offers to restore it from the tool
    /// manifest or to install it as a local tool. False when it can't run.
    /// </summary>
    private async Task<bool> EnsureSvcutilAsync(string project)
    {
        var tool = await ServiceReferences.WcfToolAsync(project, install: false, CancellationToken.None).ConfigureAwait(true);
        if (tool.Available)
        {
            return true;
        }

        var question = tool.ManifestListsTool
            ? Localizer.Get("msg.webTools.restoreSvcutil", "dotnet-svcutil is in the tool manifest but isn't restored. Restore the .NET tools now?")
            : Localizer.Get("msg.webTools.installSvcutil", "WCF references are generated by dotnet-svcutil, which isn't installed. Install it as a local tool of this repository (dotnet tool install dotnet-svcutil)?");
        if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.webTools.svcutilTitle", "dotnet-svcutil"), question,
                tool.ManifestListsTool ? Localizer.Get("msg.webTools.restore", "Restore") : Localizer.Get("msg.webTools.install", "Install")).ConfigureAwait(true))
        {
            return false;
        }

        tool = await ServiceReferences.WcfToolAsync(project, install: true, CancellationToken.None).ConfigureAwait(true);
        return tool.Available ? true : throw new InvalidOperationException(tool.Message ?? "dotnet-svcutil isn't available.");
    }
}
