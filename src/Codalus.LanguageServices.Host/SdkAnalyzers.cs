using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace Codalus.LanguageServices.Host;

/// <summary>
/// Analyzers and source generators that ship in an SDK (the Razor generator, in <c>sdk/&lt;version&gt;/Sdks/…</c>) are
/// built against that SDK's compiler. When it is newer than this host's Roslyn (a new .NET's SDK, before Codalus
/// moves to it, ADR 0025), the same folder of another installed SDK that this Roslyn can load stands in for it.
/// </summary>
internal static class SdkAnalyzers
{
    private static readonly ConcurrentDictionary<string, Lazy<string?>> s_substitutes = new(Paths.Comparer);

    /// <summary>The version of <c>Microsoft.CodeAnalysis</c> this host has.</summary>
    public static Version HostRoslyn { get; } = typeof(Compilation).Assembly.GetName().Version!;

    /// <summary>
    /// The analyzer to load for <paramref name="path"/>: itself, or the same file in another SDK's folder (null when
    /// that folder doesn't have it). <paramref name="log"/> hears about each substitution once.
    /// </summary>
    public static string? Resolve(string path, Action<string>? log, Version? hostRoslyn = null)
    {
        var folder = Path.GetDirectoryName(path)!;
        if (SdkFolder(folder) is not var (sdks, version, relative))
        {
            return path;
        }

        var roslyn = hostRoslyn ?? HostRoslyn;
        var substitute = s_substitutes.GetOrAdd(folder + "|" + roslyn, _ => new Lazy<string?>(() => FindSubstitute(folder, sdks, version, relative, roslyn, log))).Value;
        if (substitute is null)
        {
            return path;
        }

        var candidate = Path.Combine(substitute, Path.GetFileName(path));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>The newest Microsoft.CodeAnalysis version that an assembly in <paramref name="folder"/> references, or null.</summary>
    public static Version? RoslynReferencedBy(string folder)
    {
        Version? newest = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                {
                    continue;
                }

                var reader = pe.GetMetadataReader();
                foreach (var handle in reader.AssemblyReferences)
                {
                    var reference = reader.GetAssemblyReference(handle);
                    if (reader.StringComparer.Equals(reference.Name, "Microsoft.CodeAnalysis") && (newest is null || reference.Version > newest))
                    {
                        newest = reference.Version;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
            {
                // Not an assembly we can read: it decides nothing.
            }
        }

        return newest;
    }

    private static string? FindSubstitute(string folder, string sdks, string version, string relative, Version roslyn, Action<string>? log)
    {
        if (RoslynReferencedBy(folder) is not { } needed || needed <= roslyn)
        {
            return null;
        }

        var others = Directory.EnumerateDirectories(sdks)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(v => !Paths.Comparer.Equals(v, version))
            .OrderByDescending(v => Version.TryParse(v.Split('-')[0], out var parsed) ? parsed : new Version())
            .ThenBy(v => v.Contains('-', StringComparison.Ordinal)); // a release before its previews
        var problem = $"The analyzers in {folder} need version {needed} of the C# compiler, and this version of Codalus has {roslyn}.";
        foreach (var other in others)
        {
            var candidate = Path.Combine(sdks, other, relative);
            if (Directory.Exists(candidate) && RoslynReferencedBy(candidate) is { } referenced && referenced <= roslyn)
            {
                log?.Invoke($"{problem} Using those of the .NET SDK {other} instead.");
                return candidate;
            }
        }

        log?.Invoke($"{problem} No other installed .NET SDK has ones it can load, so their diagnostics and generated code are missing until Codalus is updated.");
        return null;
    }

    /// <summary><c>&lt;dotnet&gt;/sdk/&lt;version&gt;/&lt;relative&gt;</c> → the <c>sdk</c> folder, the version and the relative path; null elsewhere.</summary>
    internal static (string Sdks, string Version, string Relative)? SdkFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        for (var dir = new DirectoryInfo(full); dir?.Parent?.Parent is { } sdks; dir = dir.Parent)
        {
            // dir is <sdks>/<version>/<first part of relative>.
            var version = dir.Parent;
            if (string.Equals(sdks.Name, "sdk", StringComparison.OrdinalIgnoreCase) && version.Name.Length > 0 && char.IsAsciiDigit(version.Name[0]))
            {
                return (sdks.FullName, version.Name, Path.GetRelativePath(version.FullName, full));
            }
        }

        return null;
    }
}
