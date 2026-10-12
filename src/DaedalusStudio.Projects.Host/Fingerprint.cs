using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Evaluation;

namespace DaedalusStudio.Projects.Host;

/// <summary>
/// Hashes what an evaluation read (M34b): the SDK, the global properties, every item of each evaluation (globs
/// included, so an added file changes it), and at the end the project file, its imports and the assets file with their
/// sizes and timestamps. Environment variables are not part of it.
/// </summary>
internal sealed class Fingerprint
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public Fingerprint(string path, IReadOnlyDictionary<string, string> globalProperties, Project outer)
    {
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
        foreach (var item in project.AllEvaluatedItems)
        {
            Append(item.ItemType);
            Append(item.EvaluatedInclude);
        }
    }

    public string Finish(IEnumerable<string> imports, string? assetsFile)
    {
        foreach (var file in imports.Order(StringComparer.OrdinalIgnoreCase))
        {
            Stamp(file);
        }

        Stamp(assetsFile);
        return Convert.ToHexString(_hash.GetHashAndReset());
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
    }
}
