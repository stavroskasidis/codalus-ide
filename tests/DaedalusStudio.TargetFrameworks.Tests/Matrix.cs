using System.Reflection;

namespace DaedalusStudio.TargetFrameworks.Tests;

/// <summary>
/// One target framework of samples/TargetFrameworks (M33): its projects (<c>&lt;Id&gt;.App</c>, <c>.Lib</c>, <c>.Tests</c>),
/// the C# version it defaults to, and a statement of the next C# version, which the editor must reject with the
/// compiler's error.
/// </summary>
internal sealed record Framework(string Id, string Tfm, Version Runtime, string CSharp, string? NextFeature)
{
    public string App => Id + ".App";

    public string Lib => Id + ".Lib";

    public string Tests => Id + ".Tests";

    /// <summary>The runtime's version prefix: <c>2.1.</c>.</summary>
    public string RuntimePrefix => $"{Runtime.Major}.{Runtime.Minor}.";

    /// <summary>The <c>TargetFrameworkAttribute</c> name: <c>.NETCoreApp,Version=v2.1</c>.</summary>
    public string FrameworkName => $".NETCoreApp,Version=v{Runtime.Major}.{Runtime.Minor}";

    public override string ToString() => Tfm;
}

internal static class Matrix
{
    public static IReadOnlyList<Framework> All { get; } =
    [
        new("Core20", "netcoreapp2.0", new(2, 0), "7.3", "var next = args.Length switch { 0 => \"a\", _ => \"b\" };"),
        new("Core21", "netcoreapp2.1", new(2, 1), "7.3", "var next = args.Length switch { 0 => \"a\", _ => \"b\" };"),
        new("Core22", "netcoreapp2.2", new(2, 2), "7.3", "var next = args.Length switch { 0 => \"a\", _ => \"b\" };"),
        new("Core30", "netcoreapp3.0", new(3, 0), "8.0", "System.Text.StringBuilder next = new();"),
        new("Core31", "netcoreapp3.1", new(3, 1), "8.0", "System.Text.StringBuilder next = new();"),
        new("Net50", "net5.0", new(5, 0), "9.0", "const string a = \"a\"; const string next = $\"{a}b\";"),
        new("Net60", "net6.0", new(6, 0), "10.0", "var next = \"\"\"raw\"\"\";"),
        new("Net70", "net7.0", new(7, 0), "11.0", "int[] next = [1];"),
        new("Net80", "net8.0", new(8, 0), "12.0", "var next = \"\\e\";"),
        new("Net90", "net9.0", new(9, 0), "13.0", "var next = nameof(System.Collections.Generic.List<>);"),
        new("Net100", "net10.0", new(10, 0), "14.0", null),
    ];

    public static Framework Get(string id) => All.Single(f => f.Id == id);

    /// <summary>Theory data: every framework's id.</summary>
    public static TheoryData<string> Ids => new(All.Select(f => f.Id));

    internal static string RepoRoot => Metadata("DaedalusRepoRoot");

    internal static string Configuration => Metadata("DaedalusConfiguration");

    internal static string BuildHost => Path.Combine(RepoRoot, "src", "DaedalusStudio.Projects.Host", "bin", Configuration, "net10.0", "daedalus-buildhost.dll");

    /// <summary>The 1-based line of the <c>// [name]</c> marker.</summary>
    public static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    /// <summary>
    /// Copies samples/TargetFrameworks (or another sample) under <paramref name="root"/>, standalone as under samples/,
    /// with the repository's SDK pinned above it, and returns its folder.
    /// </summary>
    public static string CopySample(string root, string name = "TargetFrameworks")
    {
        var samples = Path.Combine(root, "samples");
        Directory.CreateDirectory(samples);
        File.Copy(Path.Combine(RepoRoot, "samples", "Directory.Build.props"), Path.Combine(samples, "Directory.Build.props"), overwrite: true);
        File.Copy(Path.Combine(RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(samples, "Directory.Packages.props"), overwrite: true);
        DaedalusStudio.Tests.FixtureSdk.Pin(samples);
        Copy(Path.Combine(RepoRoot, "samples", name), Path.Combine(samples, name));
        return Path.Combine(samples, name);
    }

    public static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    public static void DeleteQuietly(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Metadata(string key) =>
        typeof(Matrix).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
}
