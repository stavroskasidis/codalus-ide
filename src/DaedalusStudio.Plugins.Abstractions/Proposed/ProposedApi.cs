namespace DaedalusStudio.Plugins.Proposed;

/// <summary>
/// Proposed APIs (ADR 0003): not yet covered by the versioning policy and free to change in any release. A plugin
/// must set <c>"enableProposedApi": true</c>; third-party plugins also need a development build
/// (<c>--enable-proposed-api</c>). Built-in plugins may use them in every build (ADR 0010). Since API 0.2.1.
/// </summary>
public interface IProposedApi
{
    /// <summary>Test controllers for the Test Explorer.</summary>
    ITests Tests { get; }

    /// <summary>The open solution, its projects, and test candidates from the language service.</summary>
    IWorkspace Workspace { get; }

    /// <summary>Builds through the shell's build (one at a time, with the Build output).</summary>
    IBuild Build { get; }

    /// <summary>Debug sessions through the shell's debugger.</summary>
    IDebug Debug { get; }

    /// <summary>Integrated terminals: open a shell or run a command in one, send it text, read its output. Since API 0.2.3.</summary>
    ITerminals Terminals { get; }
}

/// <summary>Implemented by plugin contexts that can offer proposed APIs.</summary>
public interface IProposedApiProvider
{
    /// <summary>The proposed APIs, or null when the plugin did not enable them.</summary>
    IProposedApi? Proposed { get; }
}

/// <summary>Access to proposed APIs from an <see cref="IPluginContext"/>.</summary>
public static class ProposedApiExtensions
{
    /// <summary>The proposed APIs. Throws when the plugin's manifest doesn't set <c>enableProposedApi</c>.</summary>
    public static IProposedApi Proposed(this IPluginContext context) =>
        (context as IProposedApiProvider)?.Proposed ?? throw new InvalidOperationException(
            "Proposed APIs are not enabled for this plugin: set \"enableProposedApi\": true in its manifest (and start Daedalus Studio with --enable-proposed-api).");
}
