using Codalus.Rpc;

namespace Codalus.Workbench.Languages;

/// <summary>Locates the markup language server (JSON, XML and MSBuild, YAML, .editorconfig; ADR 0016).</summary>
public static class MarkupHostLaunch
{
    public const string EnvironmentVariable = "CODALUS_MARKUPHOST";

    /// <summary>The languages it serves (<see cref="LanguageRegistry"/> ids).</summary>
    public static readonly string[] Languages = ["json", "jsonc", "xml", "msbuild", "yaml", "editorconfig"];

    /// <summary>Resolution order: <c>CODALUS_MARKUPHOST</c> (an apphost or a .dll), then <c>markuphost/</c> next to the shell.</summary>
    public static ProcessLaunchInfo? Resolve()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv)
        {
            candidates.Add(fromEnv);
        }

        var exe = OperatingSystem.IsWindows() ? "codalus-markuphost.exe" : "codalus-markuphost";
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "markuphost", exe));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "markuphost", "codalus-markuphost.dll"));
        return candidates.FirstOrDefault(File.Exists) is { } path ? ProcessLaunchInfo.FromPath(path) : null;
    }
}
