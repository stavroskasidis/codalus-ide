using System.Globalization;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Build;

/// <summary>Where a Hot Reload session is (M15b).</summary>
public enum HotReloadState
{
    /// <summary>Building and starting the app.</summary>
    Starting,

    /// <summary>The app runs; edits are applied on save.</summary>
    Watching,

    /// <summary>A file changed; the changes are being applied.</summary>
    Applying,

    /// <summary>The last changes didn't compile: they are applied once fixed.</summary>
    Errors,

    /// <summary>An edit couldn't be applied to the running app (a rude edit): it is restarting.</summary>
    Restarting,

    /// <summary>The app exited; <c>dotnet watch</c> waits for a change to start it again.</summary>
    Exited,

    /// <summary>
    /// The last update reached the app, which reported an error applying it (a browser's "Unable to apply managed code
    /// updates"): <c>dotnet watch</c> may still say the changes were applied (SDK 10, task #90).
    /// </summary>
    NotApplied,
}

/// <summary>What a line of <c>dotnet watch</c> output says, when it says something about Hot Reload.</summary>
public sealed record HotReloadEvent(HotReloadState State, string Message, TimeSpan? Applied = null);

/// <summary>
/// Follows a <c>dotnet watch</c> session from its output (ADR 0022): its own lines start with <c>dotnet watch</c>
/// (with <c>DOTNET_WATCH_SUPPRESS_EMOJIS=1</c>, followed by <c>:</c>). The wording is matched loosely, since it changes
/// between SDKs.
/// </summary>
public static partial class HotReloadOutput
{
    [GeneratedRegex(@"^\s*dotnet watch\s*\S*\s*(.*)$")]
    private static partial Regex WatchLine();

    [GeneratedRegex(@"\bin (\d+(?:[.,]\d+)?)\s*ms\b", RegexOptions.IgnoreCase)]
    private static partial Regex Duration();

    /// <summary>
    /// The prefix the Hot Reload agent in the app writes its log with, when <c>HOTRELOAD_DELTA_CLIENT_LOG_MESSAGES</c> is
    /// set (#75): its "Loaded into process" line says the app started (with its id from SDK 11 on).
    /// </summary>
    public const string AgentLogPrefix = "[codalus-hot-reload-agent]";

    [GeneratedRegex(@"\[codalus-hot-reload-agent\] Loaded into process(?: (\d+))?:")]
    private static partial Regex AgentLoaded();

    [GeneratedRegex(@"^\s*dotnet watch\s*\S*\s*\[Browser #\d+\]\s*(.*)$")]
    private static partial Regex BrowserLine();

    [GeneratedRegex(@"^\s*(?:\[[^\]]+\]\s*)?Files? (?:updated|added|deleted|changed):\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FilesLine();

    /// <summary>Whether a line is <c>dotnet watch</c>'s own.</summary>
    public static bool IsWatchLine(string line) => WatchLine().IsMatch(line);

    /// <summary>Whether a line is the Hot Reload agent's log (<see cref="AgentLogPrefix"/>).</summary>
    public static bool IsAgentLine(string line) => line.Contains(AgentLogPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The agent's "Loaded into process" line: true with the app's process id from SDK 11 on (null from SDK 10, whose
    /// line has none); false for other lines.
    /// </summary>
    public static bool IsAgentLoaded(string line, out int? processId)
    {
        processId = null;
        if (AgentLoaded().Match(line) is not { Success: true } match)
        {
            return false;
        }

        if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            processId = id;
        }

        return true;
    }

    /// <summary>What a <c>[Browser #N]</c> line of <c>dotnet watch</c> says (its text after the prefix); null for other lines.</summary>
    public static string? BrowserMessage(string line) => BrowserLine().Match(line) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// The files a "File updated: a" or "Files updated: a, b" line of <c>dotnet watch</c> names, as it wrote them
    /// (relative to its working directory, or absolute); empty for other lines.
    /// </summary>
    public static IReadOnlyList<string> UpdatedFiles(string line)
    {
        if (WatchLine().Match(line) is not { Success: true } watch)
        {
            return [];
        }

        var message = watch.Groups[1].Value.Trim().TrimStart(':').Trim();
        return FilesLine().Match(message) is { Success: true } match
            ? [.. match.Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f.TrimEnd('.'))]
            : [];
    }

    /// <summary>
    /// A line where the app's runtime refused an update (the server under the debugger: "The runtime failed to applying
    /// the change", "Further changes won't be applied to this process").
    /// </summary>
    public static bool IsRefusal(string line)
    {
        if (WatchLine().Match(line) is not { Success: true })
        {
            return false;
        }

        var text = line.ToLowerInvariant();
        return text.Contains("failed to applying the change", StringComparison.Ordinal) || text.Contains("failed to apply the change", StringComparison.Ordinal)
            || text.Contains("further changes won't be applied", StringComparison.Ordinal);
    }

    /// <summary>"Restart is needed to apply the changes.": dotnet watch is about to restart the app for an edit.</summary>
    public static bool IsRestartNeeded(string line) =>
        WatchLine().IsMatch(line) && line.Contains("restart is needed", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Waiting for a file to change before restarting": the app exited, and dotnet watch waits for an edit.</summary>
    public static bool IsAppEnded(string line) =>
        WatchLine().IsMatch(line) && line.Contains("waiting for a file to change", StringComparison.OrdinalIgnoreCase);

    /// <summary>The event a line reports, or null (the app's own output, build output, other messages).</summary>
    public static HotReloadEvent? Parse(string line)
    {
        if (WatchLine().Match(line) is not { Success: true } match)
        {
            return null;
        }

        var message = match.Groups[1].Value.Trim().TrimStart(':').Trim();
        var text = message.ToLowerInvariant();
        if (text.Contains("changes applied", StringComparison.Ordinal) || text.Contains("hot reload succeeded", StringComparison.Ordinal)
            || text.Contains("hot reload of changes succeeded", StringComparison.Ordinal))
        {
            TimeSpan? applied = Duration().Match(message) is { Success: true } ms
                && double.TryParse(ms.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? TimeSpan.FromMilliseconds(value)
                : null;
            return new HotReloadEvent(HotReloadState.Watching, message, applied);
        }

        if (text.Contains("compilation error", StringComparison.Ordinal) || text.Contains("build failed", StringComparison.Ordinal) || text.Contains(": error ", StringComparison.Ordinal))
        {
            return new HotReloadEvent(HotReloadState.Errors, message);
        }

        // "Waiting for a file to change before restarting ...": the app exited and dotnet watch waits; it isn't restarting.
        if (text.Contains("waiting for a file to change", StringComparison.Ordinal))
        {
            return new HotReloadEvent(HotReloadState.Exited, message);
        }

        if (text.Contains("restarting", StringComparison.Ordinal) || text.Contains("rude edit", StringComparison.Ordinal)
            || text.Contains("restart is needed", StringComparison.Ordinal))
        {
            return new HotReloadEvent(HotReloadState.Restarting, message);
        }

        if (text.Contains("file updated", StringComparison.Ordinal) || text.Contains("file changed", StringComparison.Ordinal) || text.Contains("files changed", StringComparison.Ordinal))
        {
            return new HotReloadEvent(HotReloadState.Applying, message);
        }

        if (text.Contains("waiting for", StringComparison.Ordinal) || text.Contains("started", StringComparison.Ordinal) && !text.Contains("restart", StringComparison.Ordinal))
        {
            return new HotReloadEvent(HotReloadState.Watching, message);
        }

        if (text.EndsWith("exited", StringComparison.Ordinal) || text.Contains("exited with", StringComparison.Ordinal))
        {
            return new HotReloadEvent(HotReloadState.Exited, message);
        }

        return null;
    }
}

/// <summary>
/// A program running under <c>dotnet watch</c> (M15b): its Hot Reload state, from the watch's output. Events are raised
/// on the output reader's thread.
/// <para>
/// For a Blazor Web App debugged with Hot Reload (#75) it also follows the app process <c>dotnet watch</c> starts: the
/// agent's "Loaded into process" line (<see cref="AppStarted"/>), the server's runtime refusing updates under the
/// debugger (<see cref="UpdatesRefused"/>), a restart for an edit (<see cref="RestartAnnounced"/>), the app's end
/// (<see cref="AppEnded"/>), the files dotnet watch saw change (<see cref="FilesUpdated"/>) and the app's own output
/// (<see cref="AppOutput"/>).
/// </para>
/// </summary>
/// <param name="workingDirectory">The folder <c>dotnet watch</c> runs in: the relative paths it reports are resolved against it.</param>
public sealed class HotReloadSession(string? workingDirectory = null)
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _inBrowserBlock;
    private string? _browserError;
    private bool _appRunning;
    private int _appProcessId;
    private int _listening;
    private int _refused;

    public HotReloadState State { get; private set; } = HotReloadState.Starting;

    /// <summary>
    /// Completes when <c>dotnet watch</c> leaves <see cref="HotReloadState.Starting"/>: it watches for changes ("Waiting
    /// for changes"), or its start failed or ended. It applies only the changes it sees from then on: until the app is
    /// launched and its agent connected, its file watcher's events have no listener, so a file saved while it builds and
    /// starts the app is dropped and the app runs the old code. The app can already be listening by then.
    /// </summary>
    public Task Started => _started.Task;

    /// <summary>The last message from <c>dotnet watch</c> about Hot Reload.</summary>
    public string? Message { get; private set; }

    /// <summary>How long the last successful update took to apply.</summary>
    public TimeSpan? LastApplied { get; private set; }

    /// <summary>How many updates were applied to the running program.</summary>
    public int Applied { get; private set; }

    /// <summary>How many times an edit restarted the program (rude edits).</summary>
    public int Restarts { get; private set; }

    /// <summary>
    /// The process id of the app <c>dotnet watch</c> started last, from its agent's "Loaded into process" line (SDK 11
    /// on; SDK 10's line has no id); null before.
    /// </summary>
    public int? AppProcessId => Volatile.Read(ref _appProcessId) is var id and > 0 ? id : null;

    /// <summary>
    /// How many times an app of this session logged "Now listening on" (each start of the app logs it once), for a wait
    /// that needs the next start's.
    /// </summary>
    public int ListeningCount => Volatile.Read(ref _listening);

    /// <summary>
    /// Whether the app's runtime refused an update: under the debugger, the .NET runtime applies no edit (a Blazor Web
    /// App's server, #75), and <c>dotnet watch</c> stops sending that process updates.
    /// </summary>
    public bool UpdatesRefused => Volatile.Read(ref _refused) != 0;

    public event EventHandler<HotReloadEvent>? Changed;

    /// <summary>An app process of the session started: its id (null when the SDK's agent doesn't say it).</summary>
    public event Action<int?>? AppStarted;

    /// <summary>The app's runtime refused an update (<see cref="UpdatesRefused"/>), the first time.</summary>
    public event Action? Refused;

    /// <summary>"Restart is needed to apply the changes.": <c>dotnet watch</c> is about to end the app and start it again.</summary>
    public event Action? RestartAnnounced;

    /// <summary>The app exited, and <c>dotnet watch</c> waits for a file to change before it starts it again.</summary>
    public event Action? AppEnded;

    /// <summary>The files <c>dotnet watch</c> saw change (full paths), edits outside the IDE included.</summary>
    public event Action<IReadOnlyList<string>>? FilesUpdated;

    /// <summary>A line of the app's own output, while it runs (not <c>dotnet watch</c>'s, the build's or the agent's).</summary>
    public event Action<string>? AppOutput;

    /// <summary>Feeds a line of the program's output.</summary>
    public void OnLine(string line)
    {
        if (HotReloadOutput.IsAgentLine(line))
        {
            if (HotReloadOutput.IsAgentLoaded(line, out var processId))
            {
                Volatile.Write(ref _appRunning, true);
                if (processId is { } id)
                {
                    Volatile.Write(ref _appProcessId, id);
                }

                AppStarted?.Invoke(processId);
            }

            return;
        }

        var watchLine = HotReloadOutput.IsWatchLine(line);
        if (!watchLine)
        {
            if (Volatile.Read(ref _inBrowserBlock) && line.TrimStart().StartsWith("Error:", StringComparison.Ordinal))
            {
                // The browser's error, inside a "[Browser #N]" block whose other lines are its stack.
                Volatile.Write(ref _browserError, line.Trim()["Error:".Length..].Trim());
            }

            if (line.Contains("Now listening on:", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _listening);
            }

            if (Volatile.Read(ref _appRunning))
            {
                AppOutput?.Invoke(line);
            }

            return;
        }

        if (HotReloadOutput.BrowserMessage(line) is { } browser)
        {
            Volatile.Write(ref _inBrowserBlock, true);
            if (browser.Contains("Error:", StringComparison.Ordinal) || browser.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _browserError, browser[(browser.IndexOf("Error", StringComparison.OrdinalIgnoreCase) + 5)..].TrimStart(':', ' '));
            }

            return;
        }

        Volatile.Write(ref _inBrowserBlock, false);
        if (HotReloadOutput.IsRefusal(line) && Interlocked.Exchange(ref _refused, 1) == 0)
        {
            Refused?.Invoke();
        }

        var files = HotReloadOutput.UpdatedFiles(line);
        if (files.Count > 0)
        {
            Volatile.Write(ref _browserError, null);
            FilesUpdated?.Invoke([.. files.Select(Resolve)]);
        }

        if (HotReloadOutput.IsRestartNeeded(line))
        {
            Volatile.Write(ref _appRunning, false);
            RestartAnnounced?.Invoke();
        }
        else if (HotReloadOutput.IsAppEnded(line))
        {
            Volatile.Write(ref _appRunning, false);
            AppEnded?.Invoke();
        }

        if (HotReloadOutput.Parse(line) is not { } e)
        {
            return;
        }

        if (e.Applied is not null && Interlocked.Exchange(ref _browserError, null) is { } error)
        {
            // The browser refused the update this "changes applied" reports (SDK 10 says applied regardless).
            e = new HotReloadEvent(HotReloadState.NotApplied, $"The browser couldn't apply the changes: {error}");
        }

        lock (_gate)
        {
            State = e.State;
            Message = e.Message;
            if (e.Applied is { } applied)
            {
                LastApplied = applied;
                Applied++;
            }

            if (e.State == HotReloadState.Restarting)
            {
                Restarts++;
            }
        }

        _started.TrySetResult();
        Changed?.Invoke(this, e);
    }

    /// <summary>The refusal is about the app that ran: a new one (after a restart) takes updates again until it refuses one.</summary>
    public void ResetRefusal() => Volatile.Write(ref _refused, 0);

    private string Resolve(string path)
    {
        try
        {
            if (Path.IsPathRooted(path) || workingDirectory is null)
            {
                return Path.GetFullPath(path);
            }

            var resolved = Path.GetFullPath(Path.Combine(workingDirectory, path));
            if (!File.Exists(resolved) && (path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith(".\\", StringComparison.Ordinal)))
            {
                // dotnet watch makes a path relative by cutting its working directory off as text: a sibling folder that
                // starts with the same name comes out as "./.Client/Pages/Counter.razor" (BlazorHosted/BlazorHosted ->
                // BlazorHosted/BlazorHosted.Client). Put back together as cut. TODO(issue): report upstream (dotnet/sdk).
                var joined = Path.GetFullPath(workingDirectory.TrimEnd('/', '\\') + path[2..]);
                if (File.Exists(joined))
                {
                    return joined;
                }
            }

            return resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
