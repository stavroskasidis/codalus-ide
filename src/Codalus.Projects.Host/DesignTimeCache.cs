using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>
/// The persisted design-time build cache (M34b): a project's last successful compiler inputs per configuration,
/// platform and target framework, in <c>obj/codalus.designtime.json</c>, with the key they were built under (the
/// project's <see cref="Fingerprint"/>, its referenced projects' and the solution's properties). An open whose keys
/// match skips the design-time builds; any change to the project, an import, an item, the assets file or a referenced
/// project builds again. <c>CODALUS_DESIGNTIME_CACHE=0</c> turns it off.
/// </summary>
internal static class DesignTimeCache
{
    public const string FileName = "codalus.designtime.json";

    /// <summary>Changes when what goes into a key or an entry changes, so older caches miss.</summary>
    public const string Schema = "codalus-designtime-1";

    private static readonly Lock s_writeGate = new();

    public static bool Enabled => Environment.GetEnvironmentVariable("CODALUS_DESIGNTIME_CACHE") != "0";

    public static string Entry(string configuration, string platform, string? framework) => $"{configuration}|{platform}|{framework}";

    /// <summary>The key of a design-time build: the project's fingerprint, the framework, the properties and the referenced projects' fingerprints.</summary>
    public static string Key(string fingerprint, string? framework, IReadOnlyDictionary<string, string> properties, IEnumerable<(string Path, string? Fingerprint)> referenced)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string? value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value ?? string.Empty));
            hash.AppendData([0]);
        }

        Append(fingerprint);
        Append(framework);
        foreach (var (name, value) in properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(name + "=" + value);
        }

        foreach (var (path, print) in referenced.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            Append(path);
            Append(print ?? "missing");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>The cached inputs for <paramref name="entry"/> when their key is <paramref name="key"/> and their files still exist.</summary>
    public static CompilerInputsInfo? TryRead(string? directory, string entry, string key, string solutionDirectory) =>
        TryRead(directory, entry, key, solutionDirectory, out _);

    /// <summary>As <see cref="TryRead(string?, string, string, string)"/>; <paramref name="miss"/> says why there was no hit.</summary>
    public static CompilerInputsInfo? TryRead(string? directory, string entry, string key, string solutionDirectory, out string? miss)
    {
        miss = null;
        if (!Enabled)
        {
            miss = "off";
        }
        else if (directory is null || Read(Path.Combine(directory, FileName)) is not { } file)
        {
            miss = "no cache";
        }
        else if (!file.Entries.TryGetValue(entry, out var cached) || cached.Inputs.State != CompilerInputStates.Ok)
        {
            miss = "no entry";
        }
        else if (cached.Key != key)
        {
            miss = "inputs changed";
        }
        else if (MissingInput(cached.Inputs, solutionDirectory) is { } missing)
        {
            miss = "missing " + missing;
        }
        else
        {
            return cached.Inputs;
        }

        return null;
    }

    public static void Write(string? directory, string entry, string key, CompilerInputsInfo inputs)
    {
        if (!Enabled || directory is null || inputs.State != CompilerInputStates.Ok)
        {
            return;
        }

        var path = Path.Combine(directory, FileName);
        lock (s_writeGate)
        {
            try
            {
                var file = Read(path) ?? new DesignTimeCacheFile(Schema, []);
                file.Entries[entry] = new DesignTimeCacheEntry(key, inputs);
                Directory.CreateDirectory(directory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(file, DesignTimeCacheJsonContext.Default.DesignTimeCacheFile));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A cache that can't be written only costs the next open a design-time build.
            }
        }
    }

    private static DesignTimeCacheFile? Read(string path)
    {
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), DesignTimeCacheJsonContext.Default.DesignTimeCacheFile) is { Schema: Schema } file
                ? file
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the files a cached command line names are there: its sources (generated ones in <c>obj</c> included),
    /// analyzers, and references outside the solution (packages, reference packs). References inside it may be other
    /// projects' outputs, which needn't exist before a build.
    /// </summary>
    internal static bool InputsExist(CompilerInputsInfo inputs, string solutionDirectory) => MissingInput(inputs, solutionDirectory) is null;

    /// <summary>
    /// The files of a <c>/reference:</c> or <c>/analyzer:</c> value: the compilers take a comma-separated list (vbc's
    /// command lines always use one), commas inside quotes being part of a path.
    /// </summary>
    internal static IEnumerable<string> List(string value)
    {
        var start = 0;
        var quoted = false;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i == value.Length || (value[i] == ',' && !quoted))
            {
                if (value[start..i].Trim() is { Length: > 0 } item)
                {
                    yield return item;
                }

                start = i + 1;
            }
            else if (value[i] == '"')
            {
                quoted = !quoted;
            }
        }
    }

    /// <summary>The first file of <see cref="InputsExist"/> that is missing, or null.</summary>
    private static string? MissingInput(CompilerInputsInfo inputs, string solutionDirectory)
    {
        var directory = Path.GetDirectoryName(inputs.Path)!;
        string Full(string path) => Path.GetFullPath(Path.Combine(directory, path.Trim('"')));
        bool Under(string path, string folder) => path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        foreach (var argument in inputs.Arguments)
        {
            if (Option(argument) is { } option)
            {
                var (name, value) = option;
                if (name is "analyzer" or "a" && List(value).Select(Full).FirstOrDefault(f => !File.Exists(f)) is { } analyzer)
                {
                    return analyzer;
                }

                if (name is "reference" or "r")
                {
                    foreach (var reference in List(value))
                    {
                        var file = Full(reference[(reference.IndexOf('=', StringComparison.Ordinal) + 1)..]);
                        if (!Under(file, solutionDirectory) && !File.Exists(file))
                        {
                            return file;
                        }
                    }
                }
            }
            else if (!File.Exists(Full(argument)))
            {
                return Full(argument);
            }
        }

        return null;
    }

    /// <summary>A compiler option's name and value (<c>/reference:x.dll</c>, <c>-nowarn:1</c>), or null for a source file.</summary>
    private static (string Name, string Value)? Option(string argument)
    {
        if (argument.Length < 2 || (argument[0] != '/' && argument[0] != '-'))
        {
            return null;
        }

        var colon = argument.IndexOf(':', StringComparison.Ordinal);
        var name = colon < 0 ? argument[1..] : argument[1..colon];

        // An absolute Unix path (/home/me/a.cs) has separators where an option's name has none.
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        return (name.TrimEnd('+', '-').ToLowerInvariant(), colon < 0 ? string.Empty : argument[(colon + 1)..]);
    }
}

internal sealed record DesignTimeCacheFile(string Schema, Dictionary<string, DesignTimeCacheEntry> Entries);

internal sealed record DesignTimeCacheEntry(string Key, CompilerInputsInfo Inputs);

[JsonSerializable(typeof(DesignTimeCacheFile))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal sealed partial class DesignTimeCacheJsonContext : JsonSerializerContext;
