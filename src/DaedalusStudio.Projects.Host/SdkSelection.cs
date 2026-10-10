namespace DaedalusStudio.Projects.Host;

/// <summary>Which SDKs the build host can load (ADR 0025): an SDK's MSBuild needs that SDK's runtime or a newer one.</summary>
internal static class SdkSelection
{
    /// <summary>The rule of <c>Microsoft.Build.Locator</c>: the SDK's major and minor version at most the runtime's.</summary>
    public static bool CanLoad(Version sdk, Version runtime) =>
        sdk.Major < runtime.Major || (sdk.Major == runtime.Major && sdk.Minor <= runtime.Minor);

    /// <summary>Why the SDK the solution asks for isn't used, and what is used instead (null: nothing).</summary>
    public static string Unloadable(Version requested, Version? used, Version runtime) =>
        $"The solution's folder resolves to the .NET SDK {requested.ToString(3)}, which needs .NET {requested.Major}.{requested.Minor} or later. "
        + $"The build host runs on .NET {runtime.ToString(3)}" + (used is null ? "." : $", so it uses the .NET SDK {used.ToString(3)} instead.")
        + $" Install the .NET {requested.Major}.{requested.Minor} runtime, or pin an SDK in global.json.";
}
