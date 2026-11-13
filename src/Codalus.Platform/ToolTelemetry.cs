#if CODALUS_PROCESS_TREE_INTERNAL
namespace Codalus.Platform.Internal;
#else
namespace Codalus.Platform;
#endif

/// <summary>
/// The opt-outs of the telemetry the tools Codalus starts would send (M59, ADR 0098): the .NET SDK's, Avalonia's build
/// task's (<c>Avalonia.BuildServices</c>, in every Avalonia 12 package, which posts from every build that isn't a
/// design-time build), Microsoft.Testing.Platform's and <c>dotnet-svcutil</c>'s. Every host and tool the IDE starts gets
/// them through this one helper: the build, language, markup, debug companion, diagnostics, data, plugin, T4 and REPL
/// hosts, the previewers and designers, the remote server's hosts, and <c>dotnet watch</c>, <c>test</c>, <c>ef</c>,
/// <c>new</c>, <c>tool</c> and <c>publish</c>.
/// </summary>
/// <remarks>
/// A variable already in the environment wins, so a user who opted in stays opted in. The programs the user runs and
/// debugs, the integrated terminal's shells, external tools and external agents get nothing from here: their telemetry
/// is the user's choice. Codalus.Rpc and the hosts that compile <c>ProcessTree.cs</c> in compile this file in too, as an
/// internal copy (the same <c>CODALUS_PROCESS_TREE_INTERNAL</c>, <see cref="ProcessTree"/>'s remarks).
/// </remarks>
#if CODALUS_PROCESS_TREE_INTERNAL
internal static class ToolTelemetry
#else
public static class ToolTelemetry
#endif
{
    /// <summary>
    /// The variables, each set to <c>1</c>. <c>DOTNET_CLI_TELEMETRY_OPTOUT</c>: the .NET SDK (the CLI, MSBuild through it,
    /// <c>dotnet new</c>, <c>tool</c>, <c>publish</c>, <c>watch</c> and <c>test</c>). <c>AVALONIA_TELEMETRY_OPTOUT</c>:
    /// Avalonia's build task, whose only switch it is (no MSBuild property turns it off).
    /// <c>TESTINGPLATFORM_TELEMETRY_OPTOUT</c>: Microsoft.Testing.Platform's test programs.
    /// <c>DOTNET_SVCUTIL_TELEMETRY_OPTOUT</c>: <c>dotnet-svcutil</c>.
    /// </summary>
    public static IReadOnlyList<string> Variables { get; } =
    [
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "AVALONIA_TELEMETRY_OPTOUT",
        "TESTINGPLATFORM_TELEMETRY_OPTOUT",
        "DOTNET_SVCUTIL_TELEMETRY_OPTOUT",
    ];

    /// <summary>
    /// Avalonia's build task refuses the opt-out on Avalonia Accelerate's Community and Trial tiers, with this text in
    /// its message ("Avalonia Accelerate Community requires telemetry. To opt out, please upgrade to a paid tier.").
    /// </summary>
    public const string AvaloniaTierRefusal = "requires telemetry. To opt out";

    /// <summary>
    /// What the IDE adds after Avalonia's refusal in its build output: why, and how to let the build send it.
    /// </summary>
    public const string AvaloniaTierExplanation =
        "Codalus sets AVALONIA_TELEMETRY_OPTOUT=1 for the builds it runs, and this project's Avalonia Accelerate tier " +
        "requires telemetry. To build it, set AVALONIA_TELEMETRY_OPTOUT=0 in the environment Codalus starts with " +
        "(a value already there is kept), or use a tier that allows the opt-out.";

    /// <summary>
    /// Adds each variable that <paramref name="environment"/> doesn't already have (a process's inherited environment,
    /// as <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> holds it). A value already there, even empty, is
    /// kept.
    /// </summary>
    public static void OptOut(IDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        foreach (var name in Variables)
        {
            if (!environment.ContainsKey(name))
            {
                environment[name] = "1";
            }
        }
    }

    /// <summary>
    /// Adds each variable to <paramref name="environment"/> (the variables a process gets on top of what it inherits)
    /// that neither it nor <paramref name="inherited"/> (by default this process's own environment) has.
    /// </summary>
    public static void OptOutOnTop(IDictionary<string, string> environment, Func<string, string?>? inherited = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        inherited ??= Environment.GetEnvironmentVariable;
        foreach (var name in Variables)
        {
            if (!environment.ContainsKey(name) && inherited(name) is null)
            {
                environment[name] = "1";
            }
        }
    }

    /// <summary>
    /// The same for a POSIX shell script run on another machine (a remote test environment): one <c>export</c> that keeps
    /// a value the machine's environment already has (<c>NAME="${NAME-1}"</c>).
    /// </summary>
    public static string PosixShellExports()
    {
        var line = new System.Text.StringBuilder("export");
        foreach (var name in Variables)
        {
            line.Append(' ').Append(name).Append("=\"${").Append(name).Append("-1}\"");
        }

        return line.ToString();
    }

    /// <summary>
    /// <paramref name="environment"/> (variables on top of what a process inherits; null for none) with the opt-outs
    /// that neither it nor <paramref name="inherited"/> has.
    /// </summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string>? environment, Func<string, string?>? inherited = null)
    {
        var all = environment is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(environment, StringComparer.Ordinal);
        OptOutOnTop(all, inherited);
        return all;
    }
}
