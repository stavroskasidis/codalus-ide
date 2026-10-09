using System.Reflection;

namespace Daedalus.LanguageServices.Host.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = Metadata("DaedalusRepoRoot");

    public static string Configuration { get; } = Metadata("DaedalusConfiguration");

    public static string HostAssembly =>
        Path.Combine(Root, "src", "Daedalus.LanguageServices.Host", "bin", Configuration, "net10.0", "daedalus-lshost.dll");

    public static string BuildHostAssembly =>
        Path.Combine(Root, "src", "Daedalus.Projects.Host", "bin", Configuration, "net10.0", "daedalus-buildhost.dll");

    /// <summary>Copies samples/<paramref name="fixture"/> (and the files that keep fixtures standalone) under <paramref name="root"/>.</summary>
    public static string CopyFixture(string fixture, string root)
    {
        var samples = Path.Combine(Root, "samples");
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(samples, "Directory.Build.props"), Path.Combine(root, "Directory.Build.props"));
        File.Copy(Path.Combine(samples, "Directory.Packages.props"), Path.Combine(root, "Directory.Packages.props"));
        var target = Path.Combine(root, fixture);
        Copy(Path.Combine(samples, fixture), target);
        return target;
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string Metadata(string key) =>
        typeof(RepoPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"Missing assembly metadata '{key}'.");
}
