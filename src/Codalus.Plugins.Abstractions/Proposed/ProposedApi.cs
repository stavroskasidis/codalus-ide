namespace Codalus.Plugins.Proposed;

/// <summary>
/// Proposed APIs (ADR 0003): not yet covered by the versioning policy and free to change in any release. A plugin
/// must set <c>"enableProposedApi": true</c>; third-party plugins also need a development build
/// (<c>--enable-proposed-api</c>). Built-in plugins may use them in every build (ADR 0010).
/// <para>
/// API 1.0 promoted every proposal that built-in plugins had proved (tests, workspace, build, debug, terminals,
/// hosting, secrets and visualizers) to <see cref="IPluginContext"/>. New proposals start here.
/// </para>
/// </summary>
public interface IProposedApi
{
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
            "Proposed APIs are not enabled for this plugin: set \"enableProposedApi\": true in its manifest (and start Codalus IDE with --enable-proposed-api).");
}
