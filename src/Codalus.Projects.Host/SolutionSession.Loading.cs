using System.Text.Json;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>Opening faster (M34b): restore only when needed, and the design-time build cache.</summary>
internal sealed partial class SolutionSession
{
    /// <summary>Written next to the assets file after a restore Codalus ran: a no-op restore leaves the assets file's date alone.</summary>
    internal const string RestoreStampFile = "codalus.restore.stamp";

    private readonly HashSet<string> _forcedDesignTime = new(s_pathComparer);

    /// <summary>The design-time build cache's misses (<c>Name: reason</c>) and hits since the last completed progress (M34c).</summary>
    private readonly List<string> _cacheStatistics = [];
    private int _cacheHits;

    /// <summary>
    /// The number of projects whose packages are up to date, or null when any project may need a restore. A project is
    /// up to date when NuGet's last restore succeeded (<c>project.nuget.cache</c>), every package it expects is in the
    /// packages folder, and its assets file (or Codalus's last restore) is newer than the project file and every
    /// import outside its <c>obj</c> folder (where <c>Directory.Packages.props</c>, <c>Directory.Build.props</c> and
    /// the SDK's files are). NuGet's own no-op check compares a hash of the restore inputs; this one is stricter.
    /// </summary>
    internal int? PackagesUpToDate()
    {
        var stamps = new Dictionary<string, DateTime>(s_pathComparer);
        DateTime Stamp(string file)
        {
            if (!stamps.TryGetValue(file, out var stamp))
            {
                stamps[file] = stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MaxValue;
            }

            return stamp;
        }

        var packageFiles = new HashSet<string>(s_pathComparer);
        var count = 0;
        foreach (var path in ProjectPaths)
        {
            if (_evaluated.TryGetValue(path, out var legacy) && legacy.NetFrameworkRestore is { IsComplete: false } && legacy.Info.CannotBuildReason is null)
            {
                return null; // a non-SDK-style project lacks its packages.config packages or reference assemblies (M43a)
            }

            if (!_evaluated.TryGetValue(path, out var project) || project.Info.State != ProjectStates.Loaded || project.AssetsFile is not { } assets)
            {
                continue; // nothing NuGet restores
            }

            var obj = System.IO.Path.GetDirectoryName(assets)!;
            var restored = File.Exists(assets) ? File.GetLastWriteTimeUtc(assets) : DateTime.MinValue;
            var marker = System.IO.Path.Combine(obj, RestoreStampFile);
            if (File.Exists(marker))
            {
                restored = new DateTime(Math.Max(restored.Ticks, File.GetLastWriteTimeUtc(marker).Ticks), DateTimeKind.Utc);
            }

            if (restored == DateTime.MinValue || Stamp(path) > restored
                || project.Imports.Any(i => !ProjectFileEditor.IsSameOrUnder(i, obj) && Stamp(i) > restored)
                || !NuGetCacheSucceeded(System.IO.Path.Combine(obj, "project.nuget.cache"), packageFiles))
            {
                return null;
            }

            count++;
        }

        return packageFiles.All(File.Exists) ? count : null;
    }

    private static bool NuGetCacheSucceeded(string cache, HashSet<string> packageFiles)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(cache));
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (root.TryGetProperty("expectedPackageFiles", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    if (file.GetString() is { Length: > 0 } expected)
                    {
                        packageFiles.Add(expected);
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Records that a restore of the solution succeeded, for <see cref="PackagesUpToDate"/>.</summary>
    private void MarkRestored()
    {
        foreach (var assets in _evaluated.Values.Select(e => e.AssetsFile).OfType<string>())
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(assets)!;
                if (Directory.Exists(directory))
                {
                    var marker = System.IO.Path.Combine(directory, RestoreStampFile);
                    File.WriteAllText(marker, string.Empty);
                    File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private bool AssetsChangedSinceEvaluation(string project) =>
        !_evaluated.TryGetValue(project, out var evaluated) || AssetsStamp(project) != evaluated.AssetsStamp;

    /// <summary>The design-time build cache key of a project for a target framework, or null when it can't be known.</summary>
    private string? DesignTimeKey(string path, string? framework, IReadOnlyDictionary<string, string> properties)
    {
        if (!_evaluated.TryGetValue(path, out var project) || project.Fingerprint is not { } fingerprint)
        {
            return null;
        }

        // Every project it references, directly or not: their target paths and frameworks are in its command line.
        var referenced = new Dictionary<string, string?>(s_pathComparer);
        var pending = new Stack<string>(References(project));
        while (pending.TryPop(out var next))
        {
            if (referenced.ContainsKey(next))
            {
                continue;
            }

            var evaluated = _evaluated.GetValueOrDefault(next);
            referenced[next] = evaluated?.Fingerprint;
            foreach (var further in evaluated is null ? [] : References(evaluated))
            {
                pending.Push(further);
            }
        }

        return DesignTimeCache.Key(fingerprint, framework, properties, referenced.Select(r => (r.Key, r.Value)));

        static IEnumerable<string> References(EvaluatedProject project) =>
            project.Info.References.Where(r => r.Kind == ReferenceKinds.Project && r.Path is not null).Select(r => r.Path!).Distinct(s_pathComparer);
    }
}
