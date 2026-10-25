using Codalus.Rpc;

namespace Codalus.Workbench.Setup;

/// <summary>
/// The .NET runtime Codalus IDE itself needs (M49, ADR 0080): what its framework-dependent hosts' <c>runtimeconfig.json</c>
/// files ask for (on a release candidate, <c>Microsoft.NETCore.App 11.0.0-rc.2…</c> with <c>rollForward</c> Major). The
/// shell is Native AOT and needs no runtime; without its hosts, it can't open, build or debug anything.
/// </summary>
public static class IdeRuntime
{
    /// <summary>The hosts' folders next to the shell that run on the installed .NET (the Windows-only designers run on the app's).</summary>
    public static IReadOnlyList<string> HostFolders { get; } = ["pluginhost", "buildhost", "lshost", "replhost", "markuphost", "datahost", "debughost", "t4host", "diaghost"];

    /// <summary>
    /// The frameworks the hosts under <paramref name="baseDirectory"/> ask for, with the settings the .NET host would apply
    /// in <paramref name="environment"/>: per framework, the most demanding (highest) version. Empty when no host is there
    /// (a test's or a tool's folder).
    /// </summary>
    public static IReadOnlyList<FrameworkReference> Requirements(string baseDirectory, Func<string, string?> environment)
    {
        var references = new List<FrameworkReference>();
        foreach (var folder in HostFolders.Select(f => Path.Combine(baseDirectory, f)).Where(Directory.Exists))
        {
            try
            {
                foreach (var config in Directory.EnumerateFiles(folder, "codalus-*.runtimeconfig.json"))
                {
                    if (RuntimeConfig.Read(config, environment) is { } read)
                    {
                        references.AddRange(read.Frameworks);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. references.GroupBy(r => r.Name, StringComparer.Ordinal).Select(g => g.MaxBy(r => r.Version)!)];
    }

    /// <summary>"11.0.0-rc.3.26522.148" for a message; the channel to install is <see cref="Channel"/>.</summary>
    public static string Channel(FrameworkReference reference) => $"{reference.Version.Major}.{reference.Version.Minor}";
}
