using System.Globalization;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Workbench.Build;

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

        if (text.Contains("restarting", StringComparison.Ordinal) || text.Contains("rude edit", StringComparison.Ordinal))
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
/// A program running under <c>dotnet watch</c> (M15b): its Hot Reload state, from the watch's output. Events may be
/// raised on any thread.
/// </summary>
public sealed class HotReloadSession
{
    private readonly Lock _gate = new();

    public HotReloadState State { get; private set; } = HotReloadState.Starting;

    /// <summary>The last message from <c>dotnet watch</c> about Hot Reload.</summary>
    public string? Message { get; private set; }

    /// <summary>How long the last successful update took to apply.</summary>
    public TimeSpan? LastApplied { get; private set; }

    /// <summary>How many updates were applied to the running program.</summary>
    public int Applied { get; private set; }

    /// <summary>How many times an edit restarted the program (rude edits).</summary>
    public int Restarts { get; private set; }

    public event EventHandler<HotReloadEvent>? Changed;

    /// <summary>Feeds a line of the program's output.</summary>
    public void OnLine(string line)
    {
        if (HotReloadOutput.Parse(line) is not { } e)
        {
            return;
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

        Changed?.Invoke(this, e);
    }
}
