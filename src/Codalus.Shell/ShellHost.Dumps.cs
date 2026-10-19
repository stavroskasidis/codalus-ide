using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Dumps (M14b, ADR 0032): Debug → Open Dump File reads a Windows minidump or a Linux or macOS core dump through the
/// debug companion host (ClrMD) into a read-only dump session; Debug → Create Dump writes a dump of the debugged process.
/// </summary>
public sealed partial class ShellHost
{
    private DebugCompanion? _companion;

    /// <summary>The debug companion host (started the first time something needs it).</summary>
    public DebugCompanion Companion
    {
        get
        {
            if (_companion is null)
            {
                _companion = new DebugCompanion(DebugCompanion.Resolve);
                _companion.Log += (_, line) => Workbench.Log.AppendLine($"[debughost] {line}");
            }

            return _companion;
        }
    }

    /// <summary>Debug → Open Dump File… (a path from the palette's argument, otherwise a file picker).</summary>
    public async Task<DumpDocument?> OpenDumpAsync(string? path = null)
    {
        path ??= Window is { } window
            ? await window.PickFileAsync("Open Dump File", "Dumps", ["*.dmp", "*.mdmp", "*.core", "core.*", "*"]).ConfigureAwait(true)
            : null;
        if (path is null)
        {
            return null;
        }

        StatusBar.ShowMessage(Localizer.Format("msg.dumps.opening", "Opening {0}…", Path.GetFileName(path)), TimeSpan.FromSeconds(10));
        try
        {
            var summary = await Companion.OpenDumpAsync(path, CancellationToken.None).ConfigureAwait(true);
            var document = new DumpDocument(new DumpViewModel(summary, Companion, OpenSourceLineAsync));
            Layout.OpenDocument(document);
            return document;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.dumps.couldntOpenTheDump", "Couldn't open the dump {0}: {1}", Path.GetFileName(path), ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Debug → Create Dump: a full dump of the process being debugged, then offered for opening. Full, because a heap dump
    /// leaves out the JIT's data that maps code to source lines.
    /// </summary>
    public async Task<string?> CreateDumpAsync(string? processId = null)
    {
        var pid = int.TryParse(processId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var given) ? given : Debugger.ProcessId;
        if (pid is not { } target)
        {
            Workbench.Notifications.Info(Localizer.Get("msg.dumps.startDebuggingANetProcessFirst", "Start debugging a .NET process first: Create Dump writes a dump of the process being debugged."));
            return null;
        }

        if (processId is null && RunsElsewhere(Remote?.Display, Debugger.Processes, target) is { } where)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.dumps.onlyThisMachine", "Create Dump writes dumps of programs on this machine; this one runs on {0}.", where));
            return null;
        }

        var folder = Path.Combine(Paths.Logs, "dumps");
        var path = Path.Combine(folder, $"dump-{target}-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
        try
        {
            StatusBar.ShowMessage(Localizer.Format("msg.dumps.writingADumpOfProcess", "Writing a dump of process {0}…", target), TimeSpan.FromSeconds(10));
            var result = await Companion.CreateDumpAsync(target, path, "full", CancellationToken.None).ConfigureAwait(true);
            var size = DumpHeapRow.Bytes(result.Size);
            if (await Workbench.Notifications.Info(Localizer.Format("msg.dumps.wroteADumpOfProcess", "Wrote a {0} dump of process {1}: {2}", size, target, result.Path), "Open Dump").Result.ConfigureAwait(true) == "Open Dump")
            {
                await OpenDumpAsync(result.Path).ConfigureAwait(true);
            }

            return result.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.dumps.couldntWriteADumpOfProcess", "Couldn't write a dump of process {0}: {1}", target, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Where the debugged process <paramref name="processId"/> runs when it isn't this machine (a remote window, or a
    /// session over SSH, in a container or in WSL): the companion dumps local processes, and that id here is another one.
    /// </summary>
    internal static string? RunsElsewhere(string? remoteWindow, IReadOnlyList<DebugProcess> processes, int processId) =>
        remoteWindow ?? processes.FirstOrDefault(p => p.Id == processId && !p.Ended)?.Target;

    private async Task OpenSourceLineAsync(string file, int line) =>
        await OpenFileAsync(file, at: new Position(Math.Max(0, line - 1), 0)).ConfigureAwait(true);

    private async Task DisposeCompanionAsync()
    {
        if (_companion is { } companion)
        {
            await companion.DisposeAsync().ConfigureAwait(false);
        }
    }
}
