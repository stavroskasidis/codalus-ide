using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Workbench.Settings;

/// <summary>What a sync did.</summary>
public enum SyncOutcome
{
    /// <summary>Nothing changed on either side.</summary>
    UpToDate,

    /// <summary>The local settings went to the target.</summary>
    Pushed,

    /// <summary>The target's settings are to be applied here (<see cref="SyncResult.Remote"/>).</summary>
    Pulled,

    /// <summary>Both changed since the last sync: the user chooses (<see cref="SettingsSync.ResolveAsync"/>).</summary>
    Conflict,
}

public sealed record SyncResult(SyncOutcome Outcome, string? Remote = null);

/// <summary>
/// Settings sync (M23b) with a user-chosen backend and no Daedalus-hosted service: a folder (one that another tool
/// syncs, such as a cloud drive) or a Git repository (any remote; a gist's clone URL works). The synced document is
/// one JSON file, <see cref="FileName"/>, written by the shell. Each side's change is detected against the content of
/// the last sync (its hash, kept in <paramref name="stateFile"/>): a change on one side goes to the other, a change on
/// both asks the user. Git repositories are cloned into <paramref name="cacheFolder"/> and pushed with the user's own
/// git and credentials.
/// </summary>
public sealed class SettingsSync(GitService git, string cacheFolder, string stateFile)
{
    public const string FileName = "daedalus-settings.json";

    /// <summary>Whether <paramref name="target"/> names a Git repository rather than a folder.</summary>
    public static bool IsGitTarget(string target) =>
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || target.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("git@", StringComparison.Ordinal)
        || target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".git", StringComparison.OrdinalIgnoreCase);

    private string Clone => Path.Combine(cacheFolder, "settings-sync");

    /// <summary>
    /// Compares <paramref name="local"/> (the shell's document of the local settings) with the target's and pushes it
    /// when only it changed; returns what else to do.
    /// </summary>
    public async Task<SyncResult> SyncAsync(string target, string local, CancellationToken cancellationToken)
    {
        var remote = await ReadAsync(target, cancellationToken).ConfigureAwait(false);
        var last = LastHash(target);
        var localHash = Hash(local);
        if (remote is null)
        {
            await WriteAsync(target, local, cancellationToken).ConfigureAwait(false);
            Remember(target, localHash);
            return new(SyncOutcome.Pushed);
        }

        var remoteHash = Hash(remote);
        if (remoteHash == localHash)
        {
            Remember(target, localHash);
            return new(SyncOutcome.UpToDate);
        }

        var localChanged = localHash != last;
        var remoteChanged = remoteHash != last;
        if (localChanged && !remoteChanged)
        {
            await WriteAsync(target, local, cancellationToken).ConfigureAwait(false);
            Remember(target, localHash);
            return new(SyncOutcome.Pushed);
        }

        return new(localChanged ? SyncOutcome.Conflict : SyncOutcome.Pulled, remote); // a first sync with settings on both sides asks too
    }

    /// <summary>
    /// After <see cref="SyncOutcome.Pulled"/> was applied, or a conflict was decided: <paramref name="kept"/> is the
    /// local document as it is now; when the user kept theirs, it goes to the target.
    /// </summary>
    public async Task ResolveAsync(string target, string kept, bool push, CancellationToken cancellationToken)
    {
        if (push)
        {
            await WriteAsync(target, kept, cancellationToken).ConfigureAwait(false);
        }

        Remember(target, Hash(kept));
    }

    /// <summary>A document's hash, insensitive to formatting.</summary>
    public static string Hash(string json)
    {
        string normalized;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            normalized = JsonSerializer.Serialize(document.RootElement, SettingsJsonContext.Default.JsonElement);
        }
        catch (JsonException)
        {
            normalized = json;
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private async Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
    {
        string file;
        if (IsGitTarget(target))
        {
            await UpdateCloneAsync(target, cancellationToken).ConfigureAwait(false);
            file = Path.Combine(Clone, FileName);
        }
        else
        {
            file = Path.Combine(target, FileName);
        }

        return File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : null;
    }

    private async Task WriteAsync(string target, string content, CancellationToken cancellationToken)
    {
        var folder = IsGitTarget(target) ? Clone : target;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, FileName);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);
        if (!IsGitTarget(target))
        {
            return;
        }

        await git.RunAsync(Clone, ["add", FileName], cancellationToken).ConfigureAwait(false);
        if ((await git.RunAsync(Clone, ["diff", "--cached", "--quiet"], cancellationToken, check: false).ConfigureAwait(false)).ExitCode == 0)
        {
            return; // nothing new to commit
        }

        // The user's identity when git has one; otherwise a neutral one, for this repository's commits only.
        var identity = (await git.RunAsync(Clone, ["config", "user.email"], cancellationToken, check: false).ConfigureAwait(false)).ExitCode == 0
            ? (string[])[]
            : ["-c", "user.name=Daedalus Studio", "-c", "user.email=settings-sync@daedalus.invalid"];
        await git.RunAsync(Clone, [.. identity, "commit", "--quiet", "-m", $"Settings from {Environment.MachineName}"], cancellationToken).ConfigureAwait(false);
        await git.RunAsync(Clone, ["push", "--quiet", "-u", "origin", "HEAD"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clones the repository, or brings the clone to the remote's state (this clone is ours alone).</summary>
    private async Task UpdateCloneAsync(string url, CancellationToken cancellationToken)
    {
        var origin = Directory.Exists(Path.Combine(Clone, ".git"))
            ? (await git.RunAsync(Clone, ["remote", "get-url", "origin"], cancellationToken, check: false).ConfigureAwait(false)).Output.Trim()
            : null;
        if (origin != url)
        {
            if (Directory.Exists(Clone))
            {
                DeleteFolder(Clone);
            }

            Directory.CreateDirectory(cacheFolder);
            await git.RunAsync(cacheFolder, ["clone", "--quiet", url, Clone], cancellationToken).ConfigureAwait(false);
            return;
        }

        await git.RunAsync(Clone, ["fetch", "--quiet", "origin"], cancellationToken).ConfigureAwait(false);
        if ((await git.RunAsync(Clone, ["rev-parse", "--verify", "--quiet", "@{u}"], cancellationToken, check: false).ConfigureAwait(false)).ExitCode == 0)
        {
            await git.RunAsync(Clone, ["reset", "--quiet", "--hard", "@{u}"], cancellationToken).ConfigureAwait(false);
        }
    }

    private string? LastHash(string target)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(stateFile));
            return document.RootElement.TryGetProperty("target", out var t) && t.GetString() == target && document.RootElement.TryGetProperty("hash", out var h)
                ? h.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Remember(string target, string hash)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        var state = new Dictionary<string, string> { ["target"] = target, ["hash"] = hash, ["synced"] = DateTimeOffset.UtcNow.ToString("O") };
        File.WriteAllText(stateFile, JsonSerializer.Serialize(state, SettingsJsonContext.Default.DictionaryStringString));
    }

    /// <summary>Deletes a clone (git writes read-only object files).</summary>
    private static void DeleteFolder(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }
}
