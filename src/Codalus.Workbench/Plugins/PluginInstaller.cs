using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Codalus.Platform;
using Codalus.Plugins.Protocol;

namespace Codalus.Workbench.Plugins;

/// <summary>A plugin package opened for installing: the plugin in its staging folder, and what the user is shown first.</summary>
/// <param name="Plugin">The plugin as it would load (its state says whether it can).</param>
/// <param name="Folder">The staging folder with the plugin's files.</param>
/// <param name="Authors">The package's authors.</param>
/// <param name="Signature">The signature check's result.</param>
public sealed record PluginPackage(PluginDescription Plugin, string Folder, string? Authors, PackageSignature Signature)
{
    /// <summary>The plugin's declared permissions (ADR 0060): informative, not enforced.</summary>
    public IReadOnlyList<string> Permissions => Plugin.Manifest?.Permissions ?? [];
}

/// <summary>What <c>dotnet nuget verify</c> said about a package.</summary>
/// <param name="Signed">It has a valid signature (author or repository).</param>
/// <param name="Publisher">The author signature's subject, when there is one.</param>
/// <param name="Message">The tool's summary, for the log and the error.</param>
public sealed record PackageSignature(bool Signed, string? Publisher, string Message)
{
    public static PackageSignature NotChecked { get; } = new(false, null, "not checked");
}

/// <summary>
/// Installs plugin packages (M22c, ADR 0060) into the user plugins folder. A new plugin goes in at once; an update or an
/// uninstall touches a folder a plugin host may have open, so it waits in <c>.pending.json</c> for
/// <see cref="ApplyPending"/> at the next start.
/// </summary>
public sealed class PluginInstaller(string pluginsDirectory, PlatformKind platform)
{
    public const string PluginFolder = "plugin";
    private const string PendingFile = ".pending.json";
    private const string StagingFolder = ".staging";

    /// <summary>Checks a package's signatures; the default runs the SDK's <c>dotnet nuget verify --all</c>.</summary>
    public Func<string, CancellationToken, Task<PackageSignature>> Verify { get; init; } = VerifyWithSdkAsync;

    /// <summary>
    /// Opens a package: checks its signature when <paramref name="requireSignature"/>, extracts its <c>plugin/</c> folder
    /// into staging, and reads the plugin there. Throws <see cref="InvalidDataException"/> for a package that isn't a
    /// plugin, isn't signed when it must be, or whose id or version differ from its manifest's.
    /// </summary>
    public async Task<PluginPackage> OpenAsync(string package, bool requireSignature, CancellationToken cancellationToken)
    {
        var signature = requireSignature ? await Verify(package, cancellationToken).ConfigureAwait(false) : PackageSignature.NotChecked;
        if (requireSignature && !signature.Signed)
        {
            throw new InvalidDataException($"The package isn't signed, and its source requires a signature: {signature.Message}");
        }

        var staging = Path.Combine(pluginsDirectory, StagingFolder, Guid.NewGuid().ToString("N"));
        string? authors;
        string id;
        string version;
        using (var zip = ZipFile.OpenRead(package))
        {
            var nuspec = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/', StringComparison.Ordinal) && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("Not a NuGet package: it has no .nuspec.");
            XDocument document;
            await using (var stream = nuspec.Open())
            {
                document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            }

            var metadata = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata") ?? throw new InvalidDataException("The .nuspec has no metadata.");
            string? Value(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            id = Value("id") ?? throw new InvalidDataException("The .nuspec has no id.");
            version = Value("version") ?? throw new InvalidDataException("The .nuspec has no version.");
            authors = Value("authors");
            var types = metadata.Descendants().Where(e => e.Name.LocalName == "packageType").Select(e => (string?)e.Attribute("name"));
            if (!types.Contains(PluginGallery.PackageType, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"{id} isn't a Codalus IDE plugin (its package type isn't {PluginGallery.PackageType}).");
            }

            var prefix = PluginFolder + "/";
            var files = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith('/')).ToList();
            if (files.Count == 0)
            {
                throw new InvalidDataException($"{id} has no {PluginFolder}/ folder.");
            }

            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in files)
            {
                // Packs made on Linux may write "plugin//file": the relative path is what follows the folder's slashes.
                var target = Path.GetFullPath(Path.Combine(staging, entry.FullName[prefix.Length..].TrimStart('/')));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"{id} has a file outside its plugin folder: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        var plugin = PluginDescription.Read(staging, isBuiltIn: false, platform);
        if (plugin.Manifest is { } manifest && (!string.Equals(manifest.Id, id, StringComparison.OrdinalIgnoreCase) || !string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase)))
        {
            Discard(staging);
            throw new InvalidDataException($"The package {id} {version} holds the plugin {manifest.Id} {manifest.Version}: they must be the same.");
        }

        return new PluginPackage(plugin, staging, authors, signature);
    }

    /// <summary>
    /// Installs an opened package. Returns the plugin's folder when it was installed now, or null when it replaces an
    /// installed version and waits for the next start.
    /// </summary>
    public string? Install(PluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Plugin.State is PluginState.Invalid or PluginState.Incompatible)
        {
            throw new InvalidDataException(package.Plugin.Error ?? "The plugin can't load.");
        }

        var id = package.Plugin.Id;
        var target = Path.Combine(pluginsDirectory, id);
        if (!Directory.Exists(target))
        {
            Directory.Move(package.Folder, target);
            return target;
        }

        SetPending(id, package.Folder);
        return null;
    }

    /// <summary>Removes an installed plugin at the next start.</summary>
    public void Uninstall(string id) => SetPending(id, null);

    /// <summary>Drops a pending update or uninstall.</summary>
    public void Cancel(string id)
    {
        var pending = ReadPending();
        if (pending.Remove(id, out var staged) && staged.Length > 0)
        {
            Discard(staged);
        }

        WritePending(pending);
    }

    /// <summary>The waiting changes: each plugin id with its staged update, or null for an uninstall.</summary>
    public IReadOnlyDictionary<string, string?> Pending => ReadPending().ToDictionary(p => p.Key, p => p.Value.Length == 0 ? null : p.Value, StringComparer.Ordinal);

    /// <summary>Called first at startup, before plugins are discovered: applies the waiting updates and uninstalls.</summary>
    public IReadOnlyList<string> ApplyPending()
    {
        var applied = new List<string>();
        var pending = ReadPending();
        if (pending.Count == 0 && !Directory.Exists(Path.Combine(pluginsDirectory, StagingFolder)))
        {
            return applied;
        }
        foreach (var (id, staged) in pending)
        {
            try
            {
                var target = Path.Combine(pluginsDirectory, id);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                if (staged.Length > 0 && Directory.Exists(staged))
                {
                    Directory.Move(staged, target);
                }

                applied.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use: try again at the next start.
                continue;
            }
        }

        foreach (var id in applied)
        {
            pending.Remove(id);
        }

        WritePending(pending);
        var staging = Path.Combine(pluginsDirectory, StagingFolder);
        if (Directory.Exists(staging))
        {
            var kept = pending.Values.Where(v => v.Length > 0).Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
            foreach (var folder in Directory.EnumerateDirectories(staging).Where(f => !kept.Contains(Path.GetFullPath(f))))
            {
                Discard(folder);
            }
        }

        return applied;
    }

    /// <param name="staged">The staged update, or null to uninstall (written as an empty string).</param>
    private void SetPending(string id, string? staged)
    {
        var pending = ReadPending();
        if (pending.TryGetValue(id, out var previous) && previous.Length > 0 && previous != staged)
        {
            Discard(previous);
        }

        pending[id] = staged ?? string.Empty;
        WritePending(pending);
    }

    private Dictionary<string, string> ReadPending()
    {
        var path = Path.Combine(pluginsDirectory, PendingFile);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), PluginJsonContext.Default.DictionaryStringString) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void WritePending(Dictionary<string, string> pending)
    {
        var path = Path.Combine(pluginsDirectory, PendingFile);
        if (pending.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(pluginsDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(pending, PluginJsonContext.Default.DictionaryStringString));
    }

    private static void Discard(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary><c>dotnet nuget verify --all</c>: a valid author or repository signature, and the author's subject.</summary>
    public static async Task<PackageSignature> VerifyWithSdkAsync(string package, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "nuget", "verify", "--all", package, "-v", "n" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet didn't start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
        string? publisher = null;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Signature type: Author", StringComparison.OrdinalIgnoreCase))
            {
                publisher = lines.Skip(i).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Subject Name:", StringComparison.OrdinalIgnoreCase))?["Subject Name:".Length..].Trim();
                break;
            }
        }

        var summary = lines.Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? string.Empty;
        return new PackageSignature(process.ExitCode == 0, publisher, summary);
    }
}
