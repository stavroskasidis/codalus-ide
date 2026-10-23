using System.Text.Json.Nodes;
using Codalus.Platform;
using Codalus.Workbench.Plugins;

namespace Codalus.Plugins.Tests;

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
    public void Api_versions_agree() => Assert.Equal(PluginApi.Version, Codalus.Plugins.Protocol.PluginMethods.ApiVersion);
}

public sealed class ManifestValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-manifests-" + Guid.NewGuid().ToString("N"));

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

        var manifestPath = Path.Combine(dir, "codalus-plugin.json");
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
    public void Document_tab_menus_are_known_and_clauses_that_dont_parse_are_reported()
    {
        // documentTab/context is a documented location (API 1.0, M34e); a when-clause or enablement that doesn't parse
        // registers as "always" (PluginManager), which the author should hear about.
        var plugin = PluginDescription.Read(Variant("menus", m =>
        {
            m["contributes"]!["menus"]!["documentTab/context"] = JsonNode.Parse("""[{ "command": "hello.sayHello" }]""");
            m["contributes"]!["menus"]!["menuBar/tools"]![0]!["when"] = "editorTextFocus &&";
            m["contributes"]!["commands"]![0]!["enablement"] = "(solutionOpen";
        }), isBuiltIn: false, PlatformKind.Windows);

        Assert.Equal(PluginState.Ready, plugin.State);
        Assert.DoesNotContain(plugin.Warnings, w => w.Contains("documentTab/context", StringComparison.Ordinal));
        Assert.Contains(plugin.Warnings, w => w.Contains("the when-clause of 'hello.sayHello' doesn't parse", StringComparison.Ordinal));
        Assert.Contains(plugin.Warnings, w => w.Contains("Command 'hello.sayHello': its enablement doesn't parse", StringComparison.Ordinal));
        Assert.Equal(2, plugin.Warnings.Count);
    }

    [Fact]
    public void Incompatible_engine_ranges_are_rejected_with_a_reason()
    {
        var plugin = PluginDescription.Read(Variant("future", m => m["engines"]!["codalus"] = "^9.0.0"), false, PlatformKind.Windows);
        Assert.Equal(PluginState.Incompatible, plugin.State);
        Assert.Contains("^9.0.0", plugin.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id", "Not A Valid Id")]
    [InlineData("version", "one")]
    [InlineData("isolation", "maybe")]
    [InlineData("runsOn", "server")]
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
    public void Property_providers_external_tool_macros_and_custom_tools_are_contributed()
    {
        var dir = Variant("projectsystem", m =>
        {
            m["engines"]!["codalus"] = "^1.0.0";
            m["contributes"]!["propertyProviders"] = JsonNode.Parse("""[ { "command": "hello.properties", "when": "explorerItemKind == file" }, { "command": "" } ]""");
            m["contributes"]!["externalToolMacros"] = JsonNode.Parse("""[ { "name": "Greeting", "command": "hello.greeting", "description": "A greeting." } ]""");
            m["contributes"]!["customTools"] = JsonNode.Parse("""[ { "name": "HelloGenerator", "command": "hello.generate", "extension": ".g.cs" }, { "name": "", "command": "x" } ]""");
        });
        var plugin = PluginDescription.Read(dir, isBuiltIn: false, PlatformKind.Windows);
        Assert.Equal(PluginState.Ready, plugin.State);
        Assert.Equal("explorerItemKind == file", plugin.Manifest!.Contributes!.PropertyProviders![0].When);

        var paths = AppPaths.Resolve(PlatformKind.Windows, _ => null, Path.Combine(_root, "user"));
        paths.EnsureCreated();
        using var workbench = new Codalus.Workbench.WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var manager = new PluginManager(workbench, _ => null, "test");
        manager.AddPlugins([plugin]);
        Assert.Equal([("hello.properties")], manager.PropertyProviders.Select(p => p.Provider.Command));
        Assert.Equal(("Greeting", "hello.greeting", "A greeting."), manager.ToolMacros.Select(m => (m.Macro.Name, m.Macro.Command, m.Macro.Description)).Single());
        Assert.Equal(("HelloGenerator", "hello.generate", ".g.cs"), manager.CustomTools.Select(t => (t.Tool.Name, t.Tool.Command, t.Tool.Extension)).Single());
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
        Assert.Equal("dedicated:codalus-samples.hello", PluginDescription.Read(Variant("dedicated", m => m["isolation"] = "dedicated"), false, PlatformKind.Windows).HostGroup);
        Assert.Equal("builtin", PluginDescription.Read(PluginHostIntegrationTests.HelloWorldDir, isBuiltIn: true, PlatformKind.Windows).HostGroup);
    }

    [Fact]
    public void Workspace_plugins_get_host_groups_of_their_own()
    {
        // runsOn (API 0.3.0, ADR 0057): a workspace plugin's host can move to a remote window's server on its own.
        var workspace = PluginDescription.Read(Variant("workspace", m => m["runsOn"] = "workspace"), false, PlatformKind.Windows);
        Assert.Equal(PluginState.Ready, workspace.State);
        Assert.True(workspace.RunsOnWorkspace);
        Assert.Equal("workspace:default", workspace.HostGroup);
        Assert.Equal("workspace:dedicated:codalus-samples.hello", PluginDescription.Read(Variant("workspacededicated", m =>
        {
            m["runsOn"] = "workspace";
            m["isolation"] = "dedicated";
        }), false, PlatformKind.Windows).HostGroup);
        var ui = PluginDescription.Read(Variant("ui", m => m["runsOn"] = "ui"), false, PlatformKind.Windows);
        Assert.False(ui.RunsOnWorkspace);
        Assert.Equal("default", ui.HostGroup);
    }

    [Fact]
    public void A_retired_templates_plugin_copy_is_ignored()
    {
        // A copy of the built-in Templates plugin left in a plugins folder (New Project is the IDE's since 0.6.0, ADR 0082).
        var stale = Variant("templates", m => m["id"] = "codalus.templates");
        var retired = new List<(string Id, string Directory)>();
        var plugins = PluginDiscovery.Scan([(_root, false)], PlatformKind.Windows, allowProposedApi: false, retired);
        Assert.DoesNotContain(plugins, p => p.Id == "codalus.templates");
        Assert.Equal([("codalus.templates", stale)], retired);
        Assert.Empty(PluginDiscovery.Scan([(stale, true)], PlatformKind.Windows)); // built-in or not

        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user"));
        paths.EnsureCreated();
        using var workbench = new Codalus.Workbench.WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var manager = new PluginManager(workbench, _ => throw new InvalidOperationException("no host"), "test");
        manager.ReportRetired(retired);
        Assert.Contains($"An old copy of the Templates plugin at {stale} is ignored: New Project is part of Codalus IDE since 0.6.0.", manager.PluginLog.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_cannot_take_a_shell_command_or_setting()
    {
        var paths = AppPaths.Resolve(PlatformInfo.Current, _ => null, Path.Combine(_root, "user"));
        paths.EnsureCreated();
        using var workbench = new Codalus.Workbench.WorkbenchServices(paths, PlatformInfo.Current, watchFiles: false);
        var ran = false;
        workbench.Commands.Register(new Codalus.Workbench.Commands.CommandDescriptor("templates.newProject", "New Project…", Codalus.Workbench.Commands.ContributionSource.Shell), (_, _) =>
        {
            ran = true;
            return Task.FromResult<System.Text.Json.JsonElement?>(null);
        });
        workbench.Settings.Register([Codalus.Workbench.Settings.SettingDescriptor.Create("templates.defaultLocation", "\"\"", Codalus.Workbench.Settings.SettingType.String, Codalus.Workbench.Commands.ContributionSource.Shell)]);

        var intruder = Variant("intruder", m =>
        {
            var contributes = m["contributes"]!.AsObject();
            contributes["commands"]!.AsArray().Add(new JsonObject { ["id"] = "templates.newProject", ["title"] = "Mine" });
            contributes["settings"]!["properties"]!.AsObject()["templates.defaultLocation"] = new JsonObject { ["type"] = "string", ["default"] = "/mine" };
            contributes["keybindings"]!.AsArray().Add(new JsonObject { ["key"] = "ctrl+alt+n", ["command"] = "templates.newProject" });
        });
        var manager = new PluginManager(workbench, _ => throw new InvalidOperationException("no host"), "test");
        manager.AddPlugins(PluginDiscovery.Scan([(intruder, false)], PlatformInfo.Current));

        var command = workbench.Commands.Find("templates.newProject")!;
        Assert.Equal((Codalus.Workbench.Commands.ContributionSource.Shell, "New Project…"), (command.Source, command.Title));
        Assert.Equal(Codalus.Workbench.Commands.ContributionSource.Shell, workbench.Settings.Find("templates.defaultLocation")!.Source);
        Assert.Equal(string.Empty, workbench.Settings.GetString("templates.defaultLocation"));
        var log = manager.PluginLog.GetText();
        Assert.Contains("[codalus-samples.hello] declares templates.newProject, which Codalus IDE owns; ignored.", log, StringComparison.Ordinal);
        Assert.Contains("[codalus-samples.hello] declares the setting templates.defaultLocation, which Codalus IDE owns; ignored.", log, StringComparison.Ordinal);

        // The plugin's own commands are still its own, and its key for the shell's command runs the shell's handler.
        Assert.Equal("codalus-samples.hello", workbench.Commands.Find("hello.sayHello")!.Source.Id);
        Assert.NotNull(workbench.Keybindings.GetBinding("templates.newProject"));
        _ = workbench.Commands.ExecuteAsync("templates.newProject", TestContext.Current.CancellationToken);
        Assert.True(ran);
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
