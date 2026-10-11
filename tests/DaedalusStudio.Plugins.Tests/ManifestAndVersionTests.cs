using System.Text.Json.Nodes;
using DaedalusStudio.Platform;
using DaedalusStudio.Workbench.Plugins;

namespace DaedalusStudio.Plugins.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("^0.1.0", "0.1.0", true)]
    [InlineData("^0.1.0", "0.1.9", true)]
    [InlineData("^0.1.0", "0.2.0", false)]
    [InlineData("^1.2.3", "1.9.0", true)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData(">=0.1.0 <0.3.0", "0.2.5", true)]
    [InlineData(">=0.1.0 <0.3.0", "0.3.0", false)]
    [InlineData("0.1.x", "0.1.7", true)]
    [InlineData("1.x", "1.4.2", true)]
    [InlineData("*", "7.0.0", true)]
    [InlineData("^2.0.0 || ^0.1.0", "0.1.3", true)]
    [InlineData("0.1.0", "0.1.1", false)]
    [InlineData("^0.1.0", "0.1.0-preview", false)]
    public void Ranges(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Parse(range).Satisfies(SemanticVersion.Parse(version)));

    [Fact]
    public void Prereleases_sort_before_releases() =>
        Assert.True(SemanticVersion.Parse("1.0.0-alpha") < SemanticVersion.Parse("1.0.0"));

    [Fact]
    public void Rejects_garbage() => Assert.False(VersionRange.TryParse("^banana", out _, out _));

    [Fact]
    public void Api_versions_agree() => Assert.Equal(PluginApi.Version, DaedalusStudio.Plugins.Protocol.PluginMethods.ApiVersion);
}

public sealed class ManifestValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-manifests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A copy of HelloWorld with a modified manifest.</summary>
    private string Variant(string name, Action<JsonObject> edit)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(PluginHostIntegrationTests.HelloWorldDir))
        {
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        }

        var manifestPath = Path.Combine(dir, "daedalus-plugin.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        edit(manifest);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        return dir;
    }

    [Fact]
    public void The_sample_manifest_is_valid()
    {
        var plugin = PluginDescription.Read(PluginHostIntegrationTests.HelloWorldDir, isBuiltIn: false, PlatformKind.Windows);
        Assert.Equal(PluginState.Ready, plugin.State);
        Assert.Empty(plugin.Warnings);
        Assert.Equal("default", plugin.HostGroup);
    }

    [Fact]
    public void Incompatible_engine_ranges_are_rejected_with_a_reason()
    {
        var plugin = PluginDescription.Read(Variant("future", m => m["engines"]!["daedalus"] = "^9.0.0"), false, PlatformKind.Windows);
        Assert.Equal(PluginState.Incompatible, plugin.State);
        Assert.Contains("^9.0.0", plugin.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id", "Not A Valid Id")]
    [InlineData("version", "one")]
    [InlineData("isolation", "maybe")]
    public void Invalid_manifests_are_rejected(string property, string value)
    {
        var plugin = PluginDescription.Read(Variant(property, m => m[property] = value), false, PlatformKind.Windows);
        Assert.Equal(PluginState.Invalid, plugin.State);
        Assert.NotNull(plugin.Error);
    }

    [Fact]
    public void Hosting_providers_activate_their_plugin_implicitly_and_bad_ones_are_ignored()
    {
        var plugin = PluginDescription.Read(Variant("hosting", m => m["contributes"]!["hostingProviders"] = JsonNode.Parse("""
            [
              { "id": "gitea", "label": "Gitea", "hosts": ["gitea.example.com"], "serverSetting": "gitea.serverUrl" },
              { "id": "Not Valid", "label": "Broken" },
              { "id": "nolabel", "label": "" }
            ]
            """)), isBuiltIn: false, PlatformKind.Windows);
        Assert.Equal(PluginState.Ready, plugin.State);
        Assert.Contains("onHostingProvider:gitea", plugin.ActivationEvents);
        Assert.DoesNotContain(plugin.ActivationEvents, e => e.Contains("Not Valid", StringComparison.Ordinal) || e.EndsWith(":nolabel", StringComparison.Ordinal));
        Assert.Equal(2, plugin.Warnings.Count(w => w.Contains("hosting provider", StringComparison.Ordinal)));
        Assert.Equal("gitea.serverUrl", plugin.Manifest!.Contributes!.HostingProviders![0].ServerSetting);

        // The built-in GitHub plugin declares its provider and needs proposed APIs, which built-ins always have.
        var github = PluginDescription.Read(HostingPluginHostTests.GitHubPluginDir, isBuiltIn: true, PlatformKind.Windows);
        Assert.Equal(PluginState.Ready, github.State);
        Assert.Empty(github.Warnings);
        Assert.Contains("onHostingProvider:github", github.ActivationEvents);
    }

    [Fact]
    public void Property_providers_and_external_tool_macros_are_contributed()
    {
        var dir = Variant("projectsystem", m =>
        {
            m["engines"]!["daedalus"] = "^0.2.8";
            m["contributes"]!["propertyProviders"] = JsonNode.Parse("""[ { "command": "hello.properties", "when": "explorerItemKind == file" }, { "command": "" } ]""");
            m["contributes"]!["externalToolMacros"] = JsonNode.Parse("""[ { "name": "Greeting", "command": "hello.greeting", "description": "A greeting." } ]""");
        });
        var plugin = PluginDescription.Read(dir, isBuiltIn: false, PlatformKind.Windows);
        Assert.Equal(PluginState.Ready, plugin.State);
        Assert.Equal("explorerItemKind == file", plugin.Manifest!.Contributes!.PropertyProviders![0].When);

        var paths = AppPaths.Resolve(PlatformKind.Windows, _ => null, Path.Combine(_root, "user"));
        paths.EnsureCreated();
        using var workbench = new DaedalusStudio.Workbench.WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var manager = new PluginManager(workbench, _ => null, "test");
        manager.AddPlugins([plugin]);
        Assert.Equal([("hello.properties")], manager.PropertyProviders.Select(p => p.Provider.Command));
        Assert.Equal(("Greeting", "hello.greeting", "A greeting."), manager.ToolMacros.Select(m => (m.Macro.Name, m.Macro.Command, m.Macro.Description)).Single());
    }

    [Fact]
    public void Missing_assembly_and_proposed_api_are_rejected()
    {
        Assert.Equal(PluginState.Invalid, PluginDescription.Read(Variant("noasm", m => m["entry"]!["assembly"] = "Missing.dll"), false, PlatformKind.Windows).State);
        var proposed = Variant("proposed", m => m["enableProposedApi"] = true);
        Assert.Equal(PluginState.Invalid, PluginDescription.Read(proposed, false, PlatformKind.Windows).State);
        Assert.Equal(PluginState.Ready, PluginDescription.Read(proposed, false, PlatformKind.Windows, allowProposedApi: true).State);
    }

    [Fact]
    public void Dedicated_isolation_gets_its_own_host_group_and_builtins_share_one()
    {
        Assert.Equal("dedicated:daedalus-samples.hello", PluginDescription.Read(Variant("dedicated", m => m["isolation"] = "dedicated"), false, PlatformKind.Windows).HostGroup);
        Assert.Equal("builtin", PluginDescription.Read(PluginHostIntegrationTests.HelloWorldDir, isBuiltIn: true, PlatformKind.Windows).HostGroup);
    }

    [Fact]
    public void Discovery_prefers_the_first_root_and_reads_single_plugin_folders()
    {
        var copy = Variant("copy", m => m["version"] = "2.0.0");
        var plugins = PluginDiscovery.Scan([(copy, false), (_root, false)], PlatformKind.Windows);
        var plugin = Assert.Single(plugins);
        Assert.Equal("2.0.0", plugin.Manifest!.Version);
    }
}
