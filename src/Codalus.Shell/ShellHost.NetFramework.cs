using Codalus.LanguageServices.Protocol;
using Codalus.Projects.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// .NET Framework projects (M43a, ADR 0075, D31): they open, edit and build on every OS and run only on Windows, where
/// Start Without Debugging starts their <c>.exe</c> on the desktop CLR (the usual path, since the SDK's <c>RunCommand</c>
/// is the <c>.exe</c>). Start (debugging) debugs it on the desktop CLR (M43b, ADR 0076): the debug service sends a
/// managed <c>.exe</c> to <c>codalus-debughost --netfx-dap</c>. Convert to SDK-Style rewrites a non-SDK-style project after a preview; Check Environment reports the
/// installed .NET Framework on Windows.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>The <c>Release</c> DWORD of .NET Framework 4.x and whether 3.5 is installed (tests give stand-ins).</summary>
    internal Func<(int? Release, bool? Has35)> NetFrameworkRuntimeProbe { get; set; } = ReadNetFrameworkRuntime;

    private static (int? Release, bool? Has35) ReadNetFrameworkRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, null);
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(NetFrameworkProjects.RuntimeKey);
            using var key35 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(NetFrameworkProjects.Runtime35Key);
            return (NetFrameworkProjects.Release(key?.GetValue("Release")), NetFrameworkProjects.Release(key35?.GetValue("Install")) == 1);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, null);
        }
    }

    /// <summary>Check Environment's rows for the solution's .NET Framework projects, on Windows (running needs the runtime).</summary>
    private IReadOnlyList<ToolStatus> NetFrameworkTools()
    {
        var projects = Solutions.Projects.Values.Where(p => p.State == ProjectStates.Loaded && NetFrameworkProjects.IsNetFramework(p)).ToList();
        if (projects.Count == 0 || Workbench.Platform != Codalus.Platform.PlatformKind.Windows)
        {
            return [];
        }

        var (release, has35) = NetFrameworkRuntimeProbe();
        return NetFrameworkProjects.ToolRows(projects, release, has35);
    }

    // ------------------------------------------------------------------ Convert to SDK-style

    /// <summary>
    /// Converts a non-SDK-style project to SDK-style (the project's context menu): the converter's result is previewed
    /// as a diff with its notes, and applied on Apply (the project file and <c>AssemblyInfo.cs</c> rewritten,
    /// <c>packages.config</c> deleted), then restored.
    /// </summary>
    public async Task ConvertToSdkStyleAsync(string? argument)
    {
        if (!RequireTrustedSolution("Convert to SDK-style") || RequireProject(argument, "convert it") is not { } project)
        {
            return;
        }

        if (!Solutions.Projects.TryGetValue(project, out var info) || !info.IsNonSdkStyle)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.netFramework.alreadySdkStyle", "{0} is an SDK-style project already.", Path.GetFileNameWithoutExtension(project)));
            return;
        }

        SdkConversion conversion;
        try
        {
            var repository = info.References.FirstOrDefault(r => r.Kind == ReferenceKinds.Package && r.Path is not null)?.Path is { } package
                ? Path.GetDirectoryName(package)
                : Solutions.Solution is { } solution ? Path.Combine(solution.Directory, "packages") : null;
            conversion = await Task.Run(() => SdkStyleConverter.Convert(project, repository)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is SdkConversionException or IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.netFramework.cannotConvert", "{0} can't be converted: {1}", info.Name, ex.Message));
            return;
        }

        var edit = await ConversionEditAsync(conversion).ConfigureAwait(true);
        var details = new List<DetailLine>();
        foreach (var note in conversion.Notes)
        {
            details.Add(new DetailLine("• " + note));
        }

        details.AddRange(await PreviewAsync(edit).ConfigureAwait(true));
        var apply = Localizer.Get("msg.netFramework.convert", "Convert");
        LastConversionPreview = details;
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.netFramework.convertTitle", "Convert {0} to SDK-style", info.Name),
            Localizer.Get("msg.netFramework.convertMessage", "The project file is rewritten as an SDK-style project (Microsoft.NET.Sdk). Review the changes:"),
            [apply, Localizer.Get("msg.netFramework.cancel", "Cancel")])
        {
            Details = details,
        }).ConfigureAwait(true);
        if (choice.Button != apply || !await ApplyWorkspaceEditAsync(edit, quiet: true).ConfigureAwait(true))
        {
            return;
        }

        Workbench.Notifications.Info(Localizer.Format("msg.netFramework.converted", "Converted {0} to an SDK-style project. Its packages are restored as package references.", info.Name));
        try
        {
            await Solutions.RestoreAsync(project, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException)
        {
            Workbench.Log.AppendLine($"[netfx] The restore after converting {info.Name} didn't start: {ex.Message}");
        }
    }

    /// <summary>The last Convert to SDK-style preview's lines (tests read them).</summary>
    internal IReadOnlyList<DetailLine>? LastConversionPreview { get; private set; }

    /// <summary>The conversion as a workspace edit: line-diff edits of the project file and AssemblyInfo.cs, deletions after them.</summary>
    private async Task<WorkspaceEdit> ConversionEditAsync(SdkConversion conversion)
    {
        var changes = new List<TextDocumentEdit>();
        foreach (var (path, text) in new[] { (conversion.ProjectPath, conversion.ProjectText) }.Concat(conversion.ChangedFiles.Select(f => (f.Path, f.Text))))
        {
            var current = OpenTextOf(path) ?? (await TextFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(true)).Text;
            var version = _models.TryGetValue(path, out var model) ? model.Version : (int?)null;
            changes.Add(new TextDocumentEdit(new OptionalVersionedTextDocumentIdentifier(DocumentUris.FromPath(path), version), [.. LineEdits(current, text)]));
        }

        return new WorkspaceEdit([.. changes])
        {
            ResourceOperations = [.. conversion.DeletedFiles.Select(f => new ResourceOperation(ResourceOperationKinds.Delete, DocumentUris.FromPath(f)))],
        };
    }

    /// <summary>The edits that turn <paramref name="oldText"/> into <paramref name="newText"/>, a line diff's changed runs (each replacing whole lines).</summary>
    internal static IReadOnlyList<TextEdit> LineEdits(string oldText, string newText)
    {
        var newline = newText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var oldLines = LineDiff.Lines(oldText);
        var newLines = LineDiff.Lines(newText);
        var script = LineDiff.Diff(oldLines, newLines);
        var edits = new List<TextEdit>();
        var oldIndex = 0;
        var i = 0;
        while (i < script.Count)
        {
            if (script[i].Kind == ' ')
            {
                oldIndex++;
                i++;
                continue;
            }

            var start = oldIndex;
            var inserted = new List<string>();
            while (i < script.Count && script[i].Kind != ' ')
            {
                if (script[i].Kind == '-')
                {
                    oldIndex++;
                }
                else
                {
                    inserted.Add(newLines[script[i].NewIndex]);
                }

                i++;
            }

            var replacement = string.Concat(inserted.Select(l => l + newline));
            edits.Add(new TextEdit(new LanguageServices.Protocol.Range(new Position(start, 0), new Position(oldIndex, 0)), replacement));
        }

        return edits;
    }
}
