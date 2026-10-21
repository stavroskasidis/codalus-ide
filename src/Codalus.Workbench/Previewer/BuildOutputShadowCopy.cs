using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Previewer;

/// <summary>
/// A copy of a build output that a designer host runs on (#66, ADR 0073), so a build can replace the original while a
/// form is open. The host is started on the copy's runtimeconfig and deps.json: the deps file puts the app's own
/// assemblies on the runtime's trusted list from the deps file's folder, so the copy has to exist before the host
/// starts (a copy made by the host can't redirect those loads), and a stack trace the host prints keeps the pdb it read
/// open. The files the host needs (deps and runtimeconfig files, the top-level assemblies, <c>runtimes/</c>, satellite
/// assemblies) must copy; every other file is copied when it can be: a database another process holds, the running
/// app's log or an unreadable file is skipped. Copies live in <c>%TEMP%/codalus-winformsdesigner/&lt;pid&gt;-&lt;id&gt;/&lt;n&gt;/</c>
/// (one numbered folder per source folder, the app's first) and go when the host ends; copies a crashed IDE left
/// behind go at the next copy.
/// </summary>
public sealed class BuildOutputShadowCopy
{
    /// <summary>The folder under the temp folder that holds the copies.</summary>
    public const string RootFolderName = "codalus-winformsdesigner";

    private const int DeleteAttempts = 10;
    private static readonly TimeSpan s_deleteRetry = TimeSpan.FromMilliseconds(300);
    private static readonly StringComparison s_comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer s_comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The copies this process made that aren't deleted yet: a sweep keeps them.</summary>
    private static readonly ConcurrentDictionary<string, byte> s_live = new(s_comparer);

    private readonly List<(string Source, string Copy)> _folders;
    private readonly Dictionary<string, (long Length, DateTime Time)> _watched;
    private readonly Dictionary<string, HashSet<string>> _watchedFolders;
    private int _deleted;

    private BuildOutputShadowCopy(string folder, List<(string Source, string Copy)> folders, Dictionary<string, (long, DateTime)> watched, Dictionary<string, HashSet<string>> watchedFolders)
    {
        Folder = folder;
        _folders = folders;
        _watched = watched;
        _watchedFolders = watchedFolders;
    }

    /// <summary>The copy's folder (<c>&lt;root&gt;/&lt;pid&gt;-&lt;id&gt;</c>); the source folders are its numbered subfolders.</summary>
    public string Folder { get; }

    /// <summary>How many files were copied.</summary>
    public int Files { get; private set; }

    /// <summary>How many bytes were copied.</summary>
    public long Bytes { get; private set; }

    /// <summary>How long the copy took.</summary>
    public TimeSpan Took { get; private set; }

    /// <summary>The files that weren't copied (held by another process, unreadable), none of them one the host needs.</summary>
    public IReadOnlyList<string> Skipped { get; private set; } = [];

    /// <summary>The default root: <c>codalus-winformsdesigner</c> in the temp folder, its links resolved (macOS's <c>/var</c>).</summary>
    public static string DefaultRoot => Path.Combine(RealPath.Resolve(Path.GetTempPath()), RootFolderName);

    /// <summary>
    /// Copies <paramref name="folders"/> (the app's output first, then a designed library's when it's elsewhere) into a
    /// new copy under <paramref name="root"/>. <paramref name="assemblies"/> are the app's and the designed project's
    /// assemblies: they, their deps and runtimeconfig files and the assemblies their deps files list are what
    /// <see cref="IsOutdated"/> watches. A missing folder, or a file the host needs that doesn't copy, is a
    /// <see cref="PreviewerException"/> (and nothing is left behind). <paramref name="copyFile"/> copies one file (tests).
    /// </summary>
    public static BuildOutputShadowCopy Create(IReadOnlyList<string> folders, IReadOnlyList<string> assemblies, string? root = null, Action<string, string>? copyFile = null, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        root ??= DefaultRoot;
        copyFile ??= static (source, target) => File.Copy(source, target, overwrite: true);
        var sources = folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).Distinct(s_comparer).ToList();
        foreach (var source in sources)
        {
            if (!Directory.Exists(source))
            {
                throw new PreviewerException(Localizer.Format("msg.winFormsDesigner.outputMissing", "The designer didn't start: {0} isn't there (build the project).", source));
            }
        }

        Sweep(root);

        // What a build replaces, as it is before the copy: a file a build writes during the copy then reads as newer.
        var (watched, watchedFolders) = Watch(assemblies.Select(Path.GetFullPath).Distinct(s_comparer).ToList());
        var folder = Path.Combine(root, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        s_live[folder] = 0;
        var copy = new BuildOutputShadowCopy(folder, [.. sources.Select((s, i) => (s, Path.Combine(folder, i.ToString(CultureInfo.InvariantCulture))))], watched, watchedFolders);
        var skipped = new List<string>();
        try
        {
            foreach (var (source, target) in copy._folders)
            {
                copy.CopyFolder(source, target, string.Empty, copyFile, skipped, cancellationToken);
            }
        }
        catch
        {
            DeleteFolder(folder);
            s_live.TryRemove(folder, out _);
            throw;
        }

        copy.Skipped = skipped;
        copy.Took = started.Elapsed;
        return copy;
    }

    private void CopyFolder(string source, string target, string relative, Action<string, string> copyFile, List<string> skipped, CancellationToken cancellationToken)
    {
        string[] files;
        string[] folders;
        try
        {
            files = Directory.GetFiles(source);
            folders = Directory.GetDirectories(source);
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder of the host's files (the output itself, runtimes/, a culture's satellites) must copy.
            if (relative.Length == 0 || IsRequired(Path.Combine(relative, "x.resources.dll")))
            {
                throw new PreviewerException(Localizer.Format("msg.winFormsDesigner.copyFailed", "The designer didn't start: copying {0} to {1} failed: {2}", source, target, ex.Message), ex);
            }

            skipped.Add(source);
            return;
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var path = relative.Length == 0 ? name : Path.Combine(relative, name);
            var copied = Path.Combine(target, name);
            try
            {
                copyFile(file, copied);
                var attributes = File.GetAttributes(copied);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    // Read-only content (TFVC, Perforce) would stop the copy's deletion on Windows.
                    File.SetAttributes(copied, attributes & ~FileAttributes.ReadOnly);
                }

                Files++;
                Bytes += new FileInfo(copied).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (IsRequired(path))
                {
                    throw new PreviewerException(Localizer.Format("msg.winFormsDesigner.copyFailed", "The designer didn't start: copying {0} to {1} failed: {2}", file, target, ex.Message), ex);
                }

                skipped.Add(file);
            }
        }

        foreach (var child in folders)
        {
            var name = Path.GetFileName(child);
            if (relative.Length == 0 && (name.Equals("publish", StringComparison.OrdinalIgnoreCase) || name.Equals("app.publish", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (new DirectoryInfo(child).LinkTarget is not null)
            {
                // A link to a folder could lead back here; an output has none of its own.
                skipped.Add(child);
                continue;
            }

            CopyFolder(child, Path.Combine(target, name), relative.Length == 0 ? name : Path.Combine(relative, name), copyFile, skipped, cancellationToken);
        }
    }

    /// <summary>
    /// Whether the host needs a file of an output (its path relative to the output folder) to start and load the app:
    /// a deps or runtimeconfig file, a top-level assembly, anything under <c>runtimes/</c>, a satellite assembly in a
    /// culture's folder. Every other file is copied when it can be.
    /// </summary>
    internal static bool IsRequired(string relative)
    {
        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var name = parts[^1];
        if (parts.Length == 1)
        {
            return name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".runtimeconfig.dev.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        if (parts[0].Equals("runtimes", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return parts.Length == 2 && name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase) && IsCulture(parts[0]);
    }

    /// <summary>
    /// Whether a folder's name is a culture's (<c>fr</c>, <c>pt-BR</c>, <c>zh-Hans</c>, <c>sr-Latn-RS</c>): a language,
    /// then a script and a region, each optional. By its form, not <see cref="CultureInfo"/>: the shell runs without
    /// culture data (invariant globalization), where no culture but the invariant one exists.
    /// </summary>
    internal static bool IsCulture(string name)
    {
        var parts = name.Split('-');
        if (parts.Length > 3 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter))
        {
            return false;
        }

        var next = 1;
        if (next < parts.Length && parts[next].Length == 4 && parts[next].All(char.IsAsciiLetter))
        {
            next++;
        }

        if (next < parts.Length && ((parts[next].Length == 2 && parts[next].All(char.IsAsciiLetter)) || (parts[next].Length == 3 && parts[next].All(char.IsAsciiDigit))))
        {
            next++;
        }

        return next == parts.Length;
    }

    /// <summary>
    /// What <see cref="IsOutdated"/> compares: each assembly, its deps and runtimeconfig files, and the assemblies (and
    /// their satellites) its deps file lists next to it; with no deps file, every assembly in its folder (and a new one
    /// there counts). Content the app writes into its output, and other projects' assemblies in a shared output folder,
    /// don't count.
    /// </summary>
    private static (Dictionary<string, (long, DateTime)> Files, Dictionary<string, HashSet<string>> Folders) Watch(IReadOnlyList<string> assemblies)
    {
        var files = new HashSet<string>(s_comparer);
        var folders = new Dictionary<string, HashSet<string>>(s_comparer);
        foreach (var assembly in assemblies)
        {
            var folder = Path.GetDirectoryName(assembly)!;
            files.Add(assembly);
            files.Add(Path.ChangeExtension(assembly, ".runtimeconfig.json"));
            var deps = Path.ChangeExtension(assembly, ".deps.json");
            if (File.Exists(deps))
            {
                files.Add(deps);
                foreach (var listed in DepsAssemblies(deps))
                {
                    files.Add(Path.Combine(folder, listed));
                }
            }
            else if (!folders.ContainsKey(folder) && Directory.Exists(folder))
            {
                var top = TopLevelAssemblies(folder);
                folders[folder] = top;
                foreach (var name in top)
                {
                    files.Add(Path.Combine(folder, name));
                }
            }
        }

        var state = new Dictionary<string, (long, DateTime)>(s_comparer);
        foreach (var file in files)
        {
            if (Stat(file) is { } info)
            {
                state[file] = info;
            }
        }

        return (state, folders);
    }

    /// <summary>
    /// The files a deps file's runtime target lists, as they are in the output: each library's <c>runtime</c> assets
    /// (by file name) and <c>resources</c> (<c>&lt;locale&gt;/&lt;file&gt;</c>).
    /// </summary>
    internal static IReadOnlyList<string> DepsAssemblies(string depsFile)
    {
        var files = new List<string>();
        try
        {
            using var stream = File.OpenRead(depsFile);
            using var document = JsonDocument.Parse(stream);
            var rootElement = document.RootElement;
            if (!rootElement.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            {
                return files;
            }

            JsonElement? target = null;
            if (rootElement.TryGetProperty("runtimeTarget", out var runtimeTarget) && runtimeTarget.ValueKind == JsonValueKind.Object
                && runtimeTarget.TryGetProperty("name", out var name) && name.GetString() is { } targetName && targets.TryGetProperty(targetName, out var named))
            {
                target = named;
            }

            target ??= targets.EnumerateObject().Select(p => (JsonElement?)p.Value).FirstOrDefault();
            if (target is not { ValueKind: JsonValueKind.Object } libraries)
            {
                return files;
            }

            foreach (var library in libraries.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.Object))
            {
                if (library.Value.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object)
                {
                    files.AddRange(runtime.EnumerateObject().Select(a => Path.GetFileName(a.Name)).Where(n => n.Length > 0));
                }

                if (library.Value.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
                {
                    foreach (var resource in resources.EnumerateObject())
                    {
                        if (resource.Value.ValueKind == JsonValueKind.Object && resource.Value.TryGetProperty("locale", out var locale) && locale.GetString() is { Length: > 0 } culture)
                        {
                            files.Add(Path.Combine(culture, Path.GetFileName(resource.Name)));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return files;
    }

    private static HashSet<string> TopLevelAssemblies(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>()
                .Where(n => n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(s_comparer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(s_comparer);
        }
    }

    private static (long, DateTime)? Stat(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a build replaced what the copy holds: a watched file with another length or time, or (without a deps
    /// file) a new assembly. A deleted file doesn't count: after a Clean the designer keeps running, and the next build
    /// replaces it.
    /// </summary>
    public bool IsOutdated()
    {
        foreach (var (file, was) in _watched)
        {
            if (Stat(file) is { } now && now != was)
            {
                return true;
            }
        }

        foreach (var (folder, known) in _watchedFolders)
        {
            if (TopLevelAssemblies(folder).Any(n => !known.Contains(n)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A path inside a copied folder, in the copy (the same relative path); any other path as it is.</summary>
    public string Map(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var (source, copy) in _folders.OrderByDescending(f => f.Source.Length))
        {
            if (full.Equals(source, s_comparison))
            {
                return copy;
            }

            if (full.StartsWith(source + Path.DirectorySeparatorChar, s_comparison))
            {
                return Path.Combine(copy, full[(source.Length + 1)..]);
            }
        }

        return path;
    }

    /// <summary>Deletes the copy (its read-only files too), retrying while the host's process lets go of its files. Never throws: what's left goes at the next sweep.</summary>
    public Task DeleteAsync()
    {
        if (Interlocked.Exchange(ref _deleted, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
            {
                if (DeleteFolder(Folder))
                {
                    break;
                }

                await Task.Delay(s_deleteRetry).ConfigureAwait(false);
            }

            s_live.TryRemove(Folder, out _);
        });
    }

    /// <summary>
    /// Deletes the copies under <paramref name="root"/> that no running process uses: another process's whose process
    /// has ended (<paramref name="isRunning"/>), and this process's that it no longer uses (a deletion that failed).
    /// Returns how many went; a folder that can't be deleted stays for the next sweep.
    /// </summary>
    public static int Sweep(string root, Func<int, bool>? isRunning = null)
    {
        isRunning ??= IsRunning;
        string[] folders;
        try
        {
            folders = Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var swept = 0;
        foreach (var folder in folders)
        {
            var name = Path.GetFileName(folder);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash <= 0 || !int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            var stale = pid == Environment.ProcessId ? !s_live.ContainsKey(folder) : !isRunning(pid);
            if (stale && DeleteFolder(folder))
            {
                swept++;
            }
        }

        return swept;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Deletes a folder and everything in it, read-only files included; false when something stayed.</summary>
    private static bool DeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // TODO(issue): hard-link files unchanged since the previous copy when outputs are large (self-contained apps, vendor control suites).
}
