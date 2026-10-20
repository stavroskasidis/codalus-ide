using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Projects;

namespace Codalus.Projects.Tests;

/// <summary>
/// Item edits (ADR 0033) through the real build host, on a copy of samples/ProjectSystem: the Properties window's Build
/// Action and metadata as Visual Studio writes them under globbing, Include and Exclude, links, moves between projects,
/// Show All Files and item templates.
/// </summary>
public sealed class ItemsIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(120);
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-items-" + Guid.NewGuid().ToString("N"));
    private SolutionService _service = null!;
    private ProjectItemService _items = null!;

    private string Fixture => Path.Combine(_root, "ProjectSystem");

    private string Slnx => Path.Combine(Fixture, "ProjectSystem.slnx");

    private string App => Path.Combine(Fixture, "src", "App", "App.csproj");

    private string AppDir => Path.GetDirectoryName(App)!;

    private string Core => Path.Combine(Fixture, "src", "Core", "Core.csproj");

    private string ItemsProject => Path.Combine(Fixture, "src", "Items", "Items.csproj");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(s_timeout).Token).Token;

    public ValueTask InitializeAsync()
    {
        Copy(Path.Combine(BuildHostIntegrationTests.RepoRoot, "samples", "ProjectSystem"), Fixture);
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        Codalus.Tests.FixtureSdk.Pin(_root);
        File.WriteAllText(Path.Combine(_root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        _service = new SolutionService(() => BuildHostIntegrationTests.HostAssemblyPath, new SupervisionPolicy { InitialBackoff = TimeSpan.FromMilliseconds(50) });
        _service.Log += (_, line) => Console.Error.WriteLine("[host] " + line);
        _items = new ProjectItemService(_service);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            Copy(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private async Task OpenAsync(string? path = null, int projects = 3)
    {
        await _service.OpenAsync(path ?? Slnx, trusted: true, restore: false, Timeout());
        await WaitUntilAsync(() => _service.Progress is { Completed: true } && _service.Projects.Count == projects);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < s_timeout, "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static string Text(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    /// <summary>The project file's item groups (from the first ItemGroup on).</summary>
    private static string ItemGroups(string path) => Text(path)[Text(path).IndexOf("  <ItemGroup>", StringComparison.Ordinal)..];

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task Every_common_build_action_reports_its_item_and_metadata()
    {
        await OpenAsync(ItemsProject, projects: 1);
        var dir = Path.GetDirectoryName(ItemsProject)!;

        var notes = await _items.GetAsync(ItemsProject, Path.Combine(dir, "notes.txt"), Timeout());
        Assert.Equal(("None", true, false), (notes.ItemType, notes.FromGlob, notes.IsLinked));
        var copy = notes.Metadata.Single(m => m.Name == ItemMetadataNames.CopyToOutputDirectory);
        Assert.Equal(("PreserveNewest", "PreserveNewest"), (copy.Value, copy.ProjectValue));
        Assert.Equal([ItemOperations.Update], notes.Elements.Select(e => e.Operation));

        var content = await _items.GetAsync(ItemsProject, Path.Combine(dir, "content.txt"), Timeout());
        Assert.Equal(("Content", false), (content.ItemType, content.FromGlob));
        Assert.Equal(["remove:None", "include:Content"], content.Elements.Select(e => e.Operation + ":" + e.ItemType));

        Assert.Equal("EmbeddedResource", (await _items.GetAsync(ItemsProject, Path.Combine(dir, "data.json"), Timeout())).ItemType);
        Assert.Equal("AdditionalFiles", (await _items.GetAsync(ItemsProject, Path.Combine(dir, "analyzers.json"), Timeout())).ItemType);
        var resx = await _items.GetAsync(ItemsProject, Path.Combine(dir, "Resources.resx"), Timeout());
        Assert.Equal(("EmbeddedResource", "ResXFileCodeGenerator"), (resx.ItemType, resx.Metadata.Single(m => m.Name == ItemMetadataNames.Generator).Value));
        Assert.Contains(resx.Metadata, m => m.Name == "LastGenOutput" && m.Value == "Resources.Designer.cs");
        Assert.Equal("TextTemplatingFileGenerator", (await _items.GetAsync(ItemsProject, Path.Combine(dir, "Greeting.tt"), Timeout())).Metadata.Single(m => m.Name == ItemMetadataNames.Generator).Value);

        var linked = await _items.GetAsync(ItemsProject, Path.Combine(Fixture, "src", "Shared", "Shared.cs"), Timeout());
        Assert.Equal(("Compile", true, @"Linked\Shared.cs"), (linked.ItemType, linked.IsLinked, linked.Link));

        Assert.Equal(["None", "Compile", "Content", "EmbeddedResource", "AdditionalFiles"], content.ItemTypes.Take(5));
    }

    // ------------------------------------------------------------------ Build Action and metadata

    [Fact]
    public async Task Metadata_of_a_globbed_file_is_an_update_item_and_resets_away()
    {
        await OpenAsync();
        var data = Path.Combine(AppDir, "data.txt");
        File.WriteAllText(data, "data");
        await WaitUntilAsync(() => _service.Projects[App].Items.Any(i => i.Path == data));

        var result = await _items.SetAsync(App, data, null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = "PreserveNewest" }, Timeout());
        Assert.True(result.Changed);
        Assert.Equal([App], result.UpdatedFiles);
        Assert.Equal("""
              <ItemGroup>
                <!-- A file outside the project folder, shown under Shared\. -->
                <Compile Include="..\Shared\Shared.cs" Link="Shared\Shared.cs" />
                <Compile Remove="Excluded\**" />
                <Compile Remove="ReleaseOnly.cs" Condition="'$(Configuration)' != 'Release'" />
                <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
                <None Update="data.txt">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </None>
                <Folder Include="Empty\" />
              </ItemGroup>
            </Project>

            """.ReplaceLineEndings("\n"), ItemGroups(App)[ItemGroups(App).IndexOf("  <ItemGroup>\n    <!--", StringComparison.Ordinal)..]);

        // Unchanged value: no edit. In place: a different value. Reset: the Update item goes.
        Assert.False((await _items.SetAsync(App, data, null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = "PreserveNewest" }, Timeout())).Changed);
        await _items.SetAsync(App, data, null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = "Always" }, Timeout());
        Assert.Contains("<CopyToOutputDirectory>Always</CopyToOutputDirectory>", Text(App), StringComparison.Ordinal);
        await _items.SetAsync(App, data, null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = null }, Timeout());
        Assert.DoesNotContain("data.txt", Text(App), StringComparison.Ordinal);

        // An attribute set in place stays an attribute; the inherited value removes the definition (the minimal change).
        await _items.SetAsync(App, Path.Combine(AppDir, "appsettings.json"), null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = "Always" }, Timeout());
        Assert.Contains("<None Update=\"appsettings.json\" CopyToOutputDirectory=\"Always\" />", Text(App), StringComparison.Ordinal);
        await _items.SetAsync(App, Path.Combine(AppDir, "appsettings.json"), null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = string.Empty }, Timeout());
        Assert.DoesNotContain("appsettings.json", Text(App), StringComparison.Ordinal);
        var info = await _items.GetAsync(App, Path.Combine(AppDir, "appsettings.json"), Timeout());
        Assert.Equal(("None", null), (info.ItemType, info.Metadata.Single(m => m.Name == ItemMetadataNames.CopyToOutputDirectory).ProjectValue));
    }

    [Fact]
    public async Task Changing_the_build_action_of_a_globbed_file_writes_remove_and_include_and_back()
    {
        await OpenAsync();
        var data = Path.Combine(AppDir, "data.txt");
        File.WriteAllText(data, "data");
        await WaitUntilAsync(() => _service.Projects[App].Items.Any(i => i.Path == data));
        await _items.SetAsync(App, data, null, new Dictionary<string, string?> { [ItemMetadataNames.CopyToOutputDirectory] = "PreserveNewest" }, Timeout());

        // None → EmbeddedResource: a Remove for the None glob, an Include with the metadata carried over.
        await _items.SetAsync(App, data, "EmbeddedResource", null, Timeout());
        var text = Text(App);
        Assert.Contains("<None Remove=\"data.txt\" />", text, StringComparison.Ordinal);
        Assert.Contains("<EmbeddedResource Include=\"data.txt\">\n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>\n    </EmbeddedResource>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("None Update=\"data.txt\"", text, StringComparison.Ordinal);
        await WaitUntilAsync(() => _service.Projects[App].Items.Any(i => i.Path == data && i.Kind == "EmbeddedResource"));
        Assert.DoesNotContain(_service.Projects[App].Items, i => i.Path == data && i.Kind == "None");

        // Back to None: the glob includes it again, and the metadata becomes an Update item.
        await _items.SetAsync(App, data, "None", null, Timeout());
        text = Text(App);
        Assert.DoesNotContain("Remove=\"data.txt\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddedResource Include=\"data.txt\"", text, StringComparison.Ordinal);
        Assert.Contains("<None Update=\"data.txt\">\n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>\n    </None>", text, StringComparison.Ordinal);

        // A C# file to Content: Compile Remove, Content Include.
        var program = Path.Combine(AppDir, "Program.cs");
        await _items.SetAsync(App, program, "Content", null, Timeout());
        Assert.Contains("<Compile Remove=\"Program.cs\" />", Text(App), StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"Program.cs\" />", Text(App), StringComparison.Ordinal);
        Assert.Equal("Content", (await _items.GetAsync(App, program, Timeout())).ItemType);
    }

    // ------------------------------------------------------------------ include and exclude

    [Fact]
    public async Task Exclude_and_include_write_and_remove_remove_items()
    {
        await OpenAsync();
        var original = Text(App);
        var program = Path.Combine(AppDir, "Program.cs");

        await _items.ExcludeAsync(App, [program], Timeout());
        Assert.Contains("<Compile Remove=\"ReleaseOnly.cs\" Condition=\"'$(Configuration)' != 'Release'\" />\n    <Compile Remove=\"Program.cs\" />", Text(App), StringComparison.Ordinal);
        // The project's new items have been reported when the edit returns, not a watcher's debounce later (the
        // Solution Explorer kept an excluded folder, selected and "in the project", until then: the command sweep).
        Assert.DoesNotContain(_service.Projects[App].Items, i => i.Path == program);
        await _items.IncludeAsync(App, [program], Timeout());
        Assert.Equal(original, Text(App));
        Assert.Contains(_service.Projects[App].Items, i => i.Path == program);

        // A file under a wildcard Remove: an explicit Include.
        var ignored = Path.Combine(AppDir, "Excluded", "Ignored.cs");
        await _items.IncludeAsync(App, [ignored], Timeout());
        Assert.Contains("<Compile Include=\"Excluded\\Ignored.cs\" />", Text(App), StringComparison.Ordinal);
        Assert.Equal("Compile", (await _items.GetAsync(App, ignored, Timeout())).ItemType);
        await _items.ExcludeAsync(App, [ignored], Timeout());
        Assert.Equal(original, Text(App));

        // A folder: Remove items for each default glob, as Visual Studio writes them; Include takes them away.
        var forms = Path.Combine(AppDir, "Forms");
        await _items.ExcludeAsync(App, [forms], Timeout());
        foreach (var type in new[] { "Compile", "EmbeddedResource", "None" })
        {
            Assert.Contains($"<{type} Remove=\"Forms\\**\" />", Text(App), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(_service.Projects[App].Items, i => i.Path.StartsWith(forms, StringComparison.OrdinalIgnoreCase));
        await _items.IncludeAsync(App, [forms], Timeout());
        Assert.Equal(original, Text(App));
        Assert.Contains(_service.Projects[App].Items, i => i.Path.StartsWith(forms, StringComparison.OrdinalIgnoreCase));

        // A linked file: excluding it removes the link.
        var shared = Path.Combine(Fixture, "src", "Shared", "Shared.cs");
        await _items.ExcludeAsync(App, [shared], Timeout());
        Assert.DoesNotContain("Shared.cs", Text(App), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edits_one_after_another_never_reload_a_project_while_a_build_reads_it()
    {
        // Windows CI (develop run 39505256369): an edit reloaded the project's XML while the previous edit's design-time
        // build read it from the same cache, and the edit failed with a NullReferenceException in XmlNode.RemoveChild.
        await OpenAsync();
        var program = Path.Combine(AppDir, "Program.cs");
        var original = Text(App);
        for (var i = 0; i < 20; i++)
        {
            await _items.ExcludeAsync(App, [program], Timeout());
            await _items.IncludeAsync(App, [program], Timeout());
        }

        Assert.Equal(original, Text(App));
        await WaitUntilAsync(() => _service.Projects[App].Items.Any(item => item.Path == program));
    }

    [Fact]
    public async Task Add_existing_item_copies_or_links()
    {
        await OpenAsync();
        var outside = Path.Combine(Fixture, "src", "Shared", "Extra.cs");
        File.WriteAllText(outside, "namespace Shared;\n\ninternal static class Extra;\n");

        var linked = await _items.AddExistingAsync(App, Path.Combine(AppDir, "Shared"), [outside], asLink: true, Timeout());
        Assert.Empty(linked.CreatedPaths);
        Assert.Contains("<Compile Include=\"..\\Shared\\Extra.cs\" Link=\"Shared\\Extra.cs\" />", Text(App), StringComparison.Ordinal);
        await WaitUntilAsync(() => _service.Projects[App].Items.Any(i => i.Path == outside && i.Link == @"Shared\Extra.cs"));
        var again = await _items.AddExistingAsync(App, AppDir, [outside], asLink: true, Timeout()); // already linked
        Assert.Empty(again.UpdatedFiles);

        var before = Text(App);
        var copied = await _items.AddExistingAsync(App, Path.Combine(AppDir, "Forms"), [outside], asLink: false, Timeout());
        Assert.Equal([Path.Combine(AppDir, "Forms", "Extra.cs")], copied.CreatedPaths);
        Assert.True(File.Exists(copied.CreatedPaths[0]));
        Assert.Equal(before, Text(App)); // the glob includes the copy
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _items.AddExistingAsync(App, Path.Combine(AppDir, "Forms"), [outside], asLink: false, Timeout()));
    }

    // ------------------------------------------------------------------ moves between projects

    [Fact]
    public async Task Moving_a_file_into_another_project_carries_its_items()
    {
        await OpenAsync();
        var appsettings = Path.Combine(AppDir, "appsettings.json");
        var coreDir = Path.GetDirectoryName(Core)!;

        // An Update item (CopyToOutputDirectory) moves to the target project.
        await _service.MoveAsync([appsettings], coreDir, copy: false, Timeout());
        Assert.DoesNotContain("appsettings.json\"", Text(App), StringComparison.Ordinal);
        Assert.Contains("<None Update=\"appsettings.json\">\n      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>\n    </None>", Text(Core), StringComparison.Ordinal);

        // A non-default Build Action: Remove + Include in the target; a copy leaves the source as it was.
        var data = Path.Combine(AppDir, "data.txt");
        File.WriteAllText(data, "data");
        await WaitUntilAsync(() => _service.Projects[App].Items.Any(i => i.Path == data));
        await _items.SetAsync(App, data, "EmbeddedResource", null, Timeout());
        var app = Text(App);
        await _service.MoveAsync([data], coreDir, copy: true, Timeout());
        Assert.Equal(app, Text(App));
        Assert.Contains("<None Remove=\"data.txt\" />", Text(Core), StringComparison.Ordinal);
        Assert.Contains("<EmbeddedResource Include=\"data.txt\" />", Text(Core), StringComparison.Ordinal);
        await WaitUntilAsync(() => _service.Projects[Core].Items.Any(i => i.Path == Path.Combine(coreDir, "data.txt") && i.Kind == "EmbeddedResource"));
    }

    // ------------------------------------------------------------------ Show All Files

    [Fact]
    public async Task The_tree_lists_files_that_are_not_items()
    {
        await OpenAsync();
        var tree = await _items.ListTreeAsync(AppDir, Timeout());
        Assert.False(tree.Truncated);
        Assert.Contains(tree.Entries, e => e.Path == Path.Combine(AppDir, "Excluded", "Ignored.cs") && !e.IsDirectory);
        Assert.Contains(tree.Entries, e => e.Path == Path.Combine(AppDir, "Excluded") && e.IsDirectory);
        Assert.DoesNotContain(_service.Projects[App].Items, i => i.Path == Path.Combine(AppDir, "Excluded", "Ignored.cs"));

        var limited = await _items.ListTreeAsync(Fixture, Timeout(), maxEntries: 3);
        Assert.True(limited.Truncated);
        Assert.Equal(3, limited.Entries.Length);

        var changed = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.FilesChanged += (_, directories) => changed.TrySetResult(directories);
        File.WriteAllText(Path.Combine(AppDir, "Excluded", "New.txt"), "new");
        Assert.Contains(Path.Combine(AppDir, "Excluded"), await changed.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ folder mode

    [Fact]
    public async Task A_folder_opens_without_projects_then_loads_builds_and_remembers_them()
    {
        var folder = Path.Combine(Fixture, "FolderOnly");
        var solution = await _service.OpenAsync(folder, trusted: true, restore: false, Timeout());
        Assert.Equal((SolutionFormats.Folder, folder, "FolderOnly"), (solution.Format, solution.Path, solution.Name));
        Assert.Equal(folder, solution.Directory);
        Assert.Empty(solution.Projects);

        var discovered = await _items.DiscoverProjectsAsync(Timeout());
        var tool = Path.Combine(folder, "src", "Tool", "Tool.csproj");
        var lib = Path.Combine(folder, "src", "Lib", "Lib.csproj");
        Assert.Equal([lib, tool], discovered);

        await _items.SetFolderProjectsAsync(discovered, Timeout());
        await WaitUntilAsync(() => _service.Solution!.Projects.Length == 2 && _service.Projects.Count == 2 && _service.Projects.Values.All(p => p.State == ProjectStates.Loaded));
        Assert.Contains("src/Tool/Tool.csproj", File.ReadAllText(Path.Combine(folder, ".codalus", "folder.json")), StringComparison.Ordinal);

        // A build of both, through a generated solution.
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _service.BuildOutput += (_, line) => output.Enqueue(line);
        var build = await _service.BuildAsync(BuildTargets.Build, null, Timeout());
        Assert.True(build.Succeeded, string.Join("\n", output.TakeLast(40)));
        Assert.True(File.Exists(Path.Combine(folder, ".codalus", "folder.slnx")));
        Assert.True(Directory.Exists(Path.Combine(folder, "src", "Tool", "bin")));

        // Reopened: the projects are loaded again; removing one keeps the other.
        solution = await _service.OpenAsync(folder, trusted: true, restore: false, Timeout());
        Assert.Equal(2, solution.Projects.Length);
        await _service.RemoveProjectAsync(lib, Timeout());
        await WaitUntilAsync(() => _service.Solution!.Projects.Length == 1);
        Assert.DoesNotContain("Lib.csproj", File.ReadAllText(Path.Combine(folder, ".codalus", "folder.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untrusted_folder_lists_its_files_but_evaluates_nothing()
    {
        var folder = Path.Combine(Fixture, "FolderOnly");
        Directory.CreateDirectory(Path.Combine(folder, ".codalus"));
        File.WriteAllText(Path.Combine(folder, ".codalus", "folder.json"), "{ \"projects\": [\"src/Tool/Tool.csproj\"] }");
        var solution = await _service.OpenAsync(folder, trusted: false, restore: false, Timeout());
        Assert.Single(solution.Projects);
        var tree = await _items.ListTreeAsync(folder, Timeout());
        Assert.Contains(tree.Entries, e => e.Path == Path.Combine(folder, "scripts", "notes.txt"));
        Assert.Equal(2, (await _items.DiscoverProjectsAsync(Timeout())).Count);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(_service.Projects);
        await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _items.GetAsync(Path.Combine(folder, "src", "Tool", "Tool.csproj"), Path.Combine(folder, "src", "Tool", "Program.cs"), Timeout()));
    }

    // ------------------------------------------------------------------ item templates

    [Fact]
    public async Task A_dotnet_new_item_template_runs_in_the_project_folder()
    {
        var hive = Path.Combine(_root, "hive");
        Environment.SetEnvironmentVariable("CODALUS_TEMPLATE_HIVE", hive);
        await OpenAsync();
        var forms = Path.Combine(AppDir, "Forms");
        var result = await _items.InstantiateAsync(new InstantiateTemplateParams("class", "Invoice", forms, App), Timeout());
        Assert.Equal([Path.Combine(forms, "Invoice.cs")], result.CreatedPaths);
        Assert.Contains("class Invoice", File.ReadAllText(result.CreatedPaths[0]), StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<JsonRpcRemoteException>(() => _items.InstantiateAsync(new InstantiateTemplateParams("no-such-template-xyz", "X", forms, App), Timeout()));
        Assert.Contains("no-such-template-xyz", failure.Message, StringComparison.Ordinal);
    }
}
