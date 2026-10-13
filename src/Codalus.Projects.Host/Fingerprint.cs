using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Evaluation;

namespace Codalus.Projects.Host;

/// <summary>
/// Hashes what an evaluation read (M34b): the SDK, the global properties, every item of each evaluation (globs
/// included, so an added file changes it; sorted, since a glob's order follows the file system's), and at the end the
/// project file, its imports and the assets file with their sizes and timestamps. Environment variables are not part
/// of it. <c>CODALUS_DESIGNTIME_CACHE_TRACE=&lt;folder&gt;</c> writes what each fingerprint hashed there, to compare two.
/// </summary>
internal sealed class Fingerprint
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly StringBuilder? _trace = Environment.GetEnvironmentVariable("CODALUS_DESIGNTIME_CACHE_TRACE") is { Length: > 0 } ? new() : null;
    private readonly string _path;

    public Fingerprint(string path, IReadOnlyDictionary<string, string> globalProperties, Project outer)
    {
        _path = path;
        Append(DesignTimeCache.Schema);
        Append(typeof(Project).Assembly.GetName().Version?.ToString());
        Append(outer.GetPropertyValue("NETCoreSdkVersion"));
        Append(outer.GetPropertyValue("MSBuildExtensionsPath"));
        Append(path);
        foreach (var (name, value) in globalProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            Append(name + "=" + value);
        }

        Add(outer);
    }

    /// <summary>Adds an evaluation's items (the outer one, then each target framework's).</summary>
    public void Add(Project project)
    {
        Append("evaluation:" + project.GetPropertyValue("TargetFramework"));
        // On macOS and Linux MSBuild turns "obj\Debug" into "obj/Debug" only once the folder exists: one spelling.
        foreach (var item in project.AllEvaluatedItems.Select(i => i.ItemType + "|" + i.EvaluatedInclude.Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            Append(item);
        }
    }

    public string Finish(IEnumerable<string> imports, string? assetsFile)
    {
        foreach (var file in imports.Order(StringComparer.OrdinalIgnoreCase))
        {
            Stamp(file);
        }

        Stamp(assetsFile);
        var result = Convert.ToHexString(_hash.GetHashAndReset());
        if (_trace is not null && Environment.GetEnvironmentVariable("CODALUS_DESIGNTIME_CACHE_TRACE") is { } folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(_path)}-{DateTime.UtcNow:HHmmssfff}-{result[..8]}.txt"), _trace.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    private void Stamp(string? file)
    {
        Append(file);
        var info = file is null ? null : new FileInfo(file);
        Append(info is { Exists: true } ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "missing");
    }

    private void Append(string? value)
    {
        _hash.AppendData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        _hash.AppendData([0]);
        _trace?.AppendLine(value);
    }
}
