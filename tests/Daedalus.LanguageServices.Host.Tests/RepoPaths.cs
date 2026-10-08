using System.Reflection;

namespace Daedalus.LanguageServices.Host.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = Metadata("DaedalusRepoRoot");

    public static string Configuration { get; } = Metadata("DaedalusConfiguration");

    public static string SpikeSolution => Path.Combine(Root, "samples", "SpikeSolution", "SpikeSolution.slnx");

    public static string Sample(params string[] relative) =>
        Path.Combine([Root, "samples", "SpikeSolution", .. relative]);

    public static string HostAssembly =>
        Path.Combine(Root, "src", "Daedalus.LanguageServices.Host", "bin", Configuration, "net10.0", "daedalus-lshost.dll");

    private static string Metadata(string key) =>
        typeof(RepoPaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"Missing assembly metadata '{key}'.");
}
