using System.Text.Json;

namespace Codalus.Projects.Host;

/// <summary>Which SDKs the build host can load (ADR 0025, ADR 0030): an SDK's MSBuild needs that SDK's runtime or a newer one.</summary>
internal static class SdkSelection
{
    /// <summary>The rule of <c>Microsoft.Build.Locator</c>: the SDK's major and minor version at most the runtime's.</summary>
    public static bool CanLoad(Version sdk, Version runtime) =>
        sdk.Major < runtime.Major || (sdk.Major == runtime.Major && sdk.Minor <= runtime.Minor);

    /// <summary>Why the SDK the solution asks for isn't used, and what is used instead (null: nothing).</summary>
    public static string Unloadable(Version requested, string? used, Version runtime) =>
        $"The solution's folder resolves to the .NET SDK {requested.ToString(3)}, which needs .NET {requested.Major}.{requested.Minor} or later. "
        + $"The build host runs on .NET {runtime.ToString(3)}" + (used is null ? "." : $", so it uses the .NET SDK {used} instead.")
        + $" Install the .NET {requested.Major}.{requested.Minor} runtime, or pin an SDK in global.json.";

    /// <summary>
    /// The oldest SDK whose MSBuild opens and builds in the build host (M33, ADR 0030): the 2.1.200 band (MSBuild 15.7).
    /// The MSBuild of the 2.0 SDKs evaluates projects, but its compiler task starts <c>csc.exe</c> directly when it runs
    /// on a newer .NET, which fails.
    /// </summary>
    public static Version Floor { get; } = new(2, 1, 200);

    /// <summary>Whether the build host can use <paramref name="sdk"/> at all (its MSBuild runs on the host's runtime).</summary>
    public static bool AboveFloor(Version sdk) => sdk >= Floor;

    /// <summary>Why an SDK below <see cref="Floor"/> isn't used, and what is used instead.</summary>
    public static string BelowFloor(string requested, string? globalJson, string? used) =>
        $"The solution's folder resolves to the .NET SDK {requested}{(globalJson is null ? string.Empty : $" ({globalJson})")}. "
        + $"Codalus IDE opens solutions with the .NET SDK {Floor.ToString(3)} or later: the MSBuild of older SDKs doesn't build in its build host"
        + (used is null ? "." : $", so it uses the .NET SDK {used} instead, which also builds older target frameworks.")
        + $" To build with the .NET SDK {requested}, use the command line, or pin the .NET SDK {Floor.ToString(3)} or later in global.json.";

    /// <summary>What happens when <c>global.json</c> asks for an SDK that isn't installed: <c>dotnet</c> fails there.</summary>
    public static string NotInstalled(GlobalJsonSdk globalJson, string used) =>
        $"{globalJson.Path} asks for the .NET SDK {globalJson.Version} (rollForward: {globalJson.RollForward}), and no installed SDK matches: "
        + $"`dotnet build` fails in that folder. The build host uses the .NET SDK {used} instead. "
        + $"Install the .NET SDK {globalJson.Version}, or change global.json.";
}

/// <summary>
/// The <c>sdk</c> section of the <c>global.json</c> that applies to a folder (the nearest one above it), and the
/// roll-forward rules <c>dotnet</c> applies to it (M33, ADR 0030).
/// </summary>
internal sealed record GlobalJsonSdk(string Path, string Version, string RollForward, bool AllowPrerelease)
{
    /// <summary>The nearest <c>global.json</c> at or above <paramref name="directory"/> with an SDK version, or null.</summary>
    public static GlobalJsonSdk? Find(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var file = System.IO.Path.Combine(current.FullName, "global.json");
            if (!File.Exists(file))
            {
                continue;
            }

            // dotnet stops at the first global.json, whether or not it names a version.
            return Parse(file);
        }

        return null;
    }

    public static GlobalJsonSdk? Parse(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object
                || !sdk.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() is not { Length: > 0 } text)
            {
                return null;
            }

            var rollForward = sdk.TryGetProperty("rollForward", out var policy) && policy.ValueKind == JsonValueKind.String && policy.GetString() is { Length: > 0 } p ? p : "latestPatch";
            var prerelease = !sdk.TryGetProperty("allowPrerelease", out var allow) || allow.ValueKind != JsonValueKind.False;
            return new GlobalJsonSdk(file, text, rollForward, prerelease);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <c>dotnet</c> could pick the SDK <paramref name="candidate"/> (<c>8.0.425</c>, <c>11.0.100-rc.1.x</c>)
    /// under this <c>global.json</c>: the same version, or a later one within what <see cref="RollForward"/> allows.
    /// </summary>
    public bool Accepts(string candidate)
    {
        if (!TryParse(Version, out var requested) || !TryParse(candidate, out var offered))
        {
            return true;
        }

        if (offered.Prerelease && !requested.Prerelease && !AllowPrerelease)
        {
            return false;
        }

        if (offered.Number < requested.Number)
        {
            return false;
        }

        var sameBand = offered.Number.Major == requested.Number.Major && offered.Number.Minor == requested.Number.Minor && offered.Band == requested.Band;
        var sameMinor = offered.Number.Major == requested.Number.Major && offered.Number.Minor == requested.Number.Minor;
        var sameMajor = offered.Number.Major == requested.Number.Major;
        return RollForward.ToLowerInvariant() switch
        {
            "disable" => offered.Number == requested.Number && offered.Prerelease == requested.Prerelease,
            "patch" or "latestpatch" => sameBand,
            "feature" or "latestfeature" => sameMinor,
            "minor" or "latestminor" => sameMajor,
            "major" or "latestmajor" => true,
            _ => sameBand,
        };
    }

    private readonly record struct SdkVersion(System.Version Number, bool Prerelease)
    {
        public int Band => Number.Build / 100;
    }

    private static bool TryParse(string text, out SdkVersion version)
    {
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var number = dash >= 0 ? text[..dash] : text;
        if (System.Version.TryParse(number, out var parsed) && parsed.Build >= 0)
        {
            version = new SdkVersion(new System.Version(parsed.Major, parsed.Minor, parsed.Build), dash >= 0);
            return true;
        }

        version = default;
        return false;
    }
}
