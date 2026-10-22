using System.Text.Json;
using Codalus.LanguageServices.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;
using Codalus.Workbench.WinForms;

namespace Codalus.Workbench.Tests;

/// <summary>
/// M41a (ADR 0073) on every OS: which files open in the Windows Forms designer, why it doesn't start off Windows (D31),
/// how its host starts (the app's runtime, or its own for a library), and its protocol through the source-generated
/// serializer the shell (Native AOT) and the host share.
/// </summary>
public sealed class WinFormsDesignerServiceTests
{
    private static string Sample(params string[] parts) =>
        Path.Combine([DiagnosticsFixture.Metadata("CodalusRepoRoot"), "samples", "WindowsDesktop", "WinFormsApp", .. parts]);

    private static WinFormsDesignerService Service(bool windows, Func<string, bool>? trusted = null) =>
        new(new SolutionService(() => "/nonexistent/codalus-buildhost.dll"), trusted ?? (_ => true), (_, _) => Task.FromResult(true), (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()))
        {
            IsWindows = () => windows,
            DesignerHost = () => "/ide/winformsdesigner/codalus-winformsdesigner.dll",
        };

    [Fact]
    public void A_forms_code_file_opens_in_the_designer_and_other_files_dont()
    {
        var service = Service(windows: true);
        Assert.True(service.HasDesigner(Sample("OrderForm.cs")));
        Assert.True(service.HasDesigner(Sample("AboutForm.cs")));
        Assert.True(service.HasDesigner(Sample("UnreadableForm.cs"))); // the C# host says it can't be read
        Assert.True(service.HasDesigner(Sample("Controls", "CounterControl.cs")));
        Assert.False(service.HasDesigner(Sample("OrderForm.Designer.cs"))); // the designer file opens as code, as in Visual Studio
        Assert.False(service.HasDesigner(Sample("Program.cs")));
        Assert.False(service.HasDesigner(Sample("Counter.cs")));
        Assert.False(service.HasDesigner(Sample("Properties", "Resources.Designer.cs")));
        Assert.False(service.HasDesigner(Sample("OrderForm.resx")));

        Assert.Equal(Sample("OrderForm.cs"), WinFormsDesignerService.CodeFileOf(Sample("OrderForm.Designer.cs")));
        Assert.Equal(Sample("OrderForm.cs"), WinFormsDesignerService.CodeFileOf(Sample("OrderForm.cs")));
        Assert.Null(WinFormsDesignerService.CodeFileOf(Sample("Program.cs")));
    }

    [Fact]
    public async Task Off_windows_the_designer_says_windows_only_before_anything_else()
    {
        var error = await Assert.ThrowsAsync<PreviewerException>(() => Service(windows: false).ResolveAsync(Sample("OrderForm.cs"), CancellationToken.None));
        Assert.Equal(WinFormsDesignerService.WindowsOnlyMessage, error.Message);
        Assert.Contains("Windows only", error.Message, StringComparison.Ordinal);

        // On Windows: a solution first.
        var closed = await Assert.ThrowsAsync<PreviewerException>(() => Service(windows: true).ResolveAsync(Sample("OrderForm.cs"), CancellationToken.None));
        Assert.Contains("Open the solution", closed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_runs_on_the_apps_runtime_or_its_own_for_a_library()
    {
        var folder = Directory.CreateTempSubdirectory("codalus-winforms-launch-").FullName;
        try
        {
            var app = Path.Combine(folder, "App.dll");
            File.WriteAllText(app, string.Empty);
            File.WriteAllText(Path.Combine(folder, "App.runtimeconfig.json"), "{}");
            var target = new WinFormsDesignerTarget(Path.Combine(folder, "Form1.cs"), Path.Combine(folder, "App.csproj"), app, app, "/ide/winformsdesigner/codalus-winformsdesigner.dll");
            var launch = WinFormsDesignerProcess.LaunchFor(target);
            Assert.Equal(["exec", "--runtimeconfig", Path.Combine(folder, "App.runtimeconfig.json"), "--depsfile", Path.Combine(folder, "App.deps.json"), "/ide/winformsdesigner/codalus-winformsdesigner.dll"], launch.Arguments);
            Assert.Equal(folder, launch.WorkingDirectory);

            // A library with no app that references it: the host's own runtimeconfig, on the newest .NET.
            var library = WinFormsDesignerProcess.LaunchFor(target with { HostAssembly = null, ProjectAssembly = Path.Combine(folder, "Lib.dll") });
            Assert.Equal(["/ide/winformsdesigner/codalus-winformsdesigner.dll"], library.Arguments);
            Assert.Equal("Major", library.Environment!["DOTNET_ROLL_FORWARD"]);

            var load = WinFormsDesignerService.LoadParams(target, new DesignerModel { ClassName = "Form1", BaseType = "System.Windows.Forms.Form" }, "App.Form1");
            Assert.Equal([folder], load.SearchFolders);
            Assert.Equal("App.Form1", load.ClassFullName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_protocol_round_trips_through_the_shared_serializer()
    {
        var json = WinFormsDesignerJsonContext.Default;
        var model = new DesignerModel
        {
            ClassName = "Form1",
            Namespace = "App",
            BaseType = "System.Windows.Forms.Form",
            Components = [new DesignerComponent("Form1", "System.Windows.Forms.Form") { IsRoot = true }, new DesignerComponent("button1", "System.Windows.Forms.Button") { Modifier = "private" }],
            Statements =
            [
                new DesignerStatement { Kind = DesignerStatementKinds.Create, Target = "button1", Value = DesignerValue.NewOf("System.Windows.Forms.Button"), Line = 3 },
                new DesignerStatement { Kind = DesignerStatementKinds.Assign, Target = "button1", Member = "Anchor", Value = DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Left") },
                new DesignerStatement { Kind = DesignerStatementKinds.Call, Target = string.Empty, Member = "Controls.Add", Arguments = [DesignerValue.ReferenceTo("button1")] },
            ],
        };
        var load = JsonSerializer.Deserialize(JsonSerializer.Serialize(new DesignerLoadParams(model, "/a/App.dll", "App.Form1", ["/a"]), json.DesignerLoadParams), json.DesignerLoadParams)!;
        Assert.Equal("Form1", load.Model.ClassName);
        Assert.Equal(3, load.Model.Statements.Length);
        Assert.True(load.Model.Statements[1].Value!.SameAs(DesignerValue.EnumOf("System.Windows.Forms.AnchorStyles", "Top", "Left")));
        Assert.Equal("button1", load.Model.Statements[2].Arguments![0].Value);

        var change = new DesignerChangeResult(
            [new DesignerPropertyChange("button1", "Location", DesignerValue.NewOf("System.Drawing.Point", DesignerValue.Int(10), DesignerValue.Int(20))), new DesignerPropertyChange("button1", "Text", null)],
            new DesignerRenderResult(new DesignerFrame("iVBORw0KGgo=", 300, 200), [new DesignerComponentView("Form1", "System.Windows.Forms.Form", null, 0, 0, 300, 200) { IsContainer = true, ClientX = 8, ClientY = 31 }], []))
        {
            Created = new DesignerCreated("button1", "System.Windows.Forms.Button", [], string.Empty, null, null, [new DesignerPropertyValue("Text", DesignerValue.String("button1"))]) { IsControl = true },
            Removed = ["label1"],
        };
        var text = JsonSerializer.Serialize(change, json.DesignerChangeResult);
        Assert.Contains("\"clientX\":8", text, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(text, json.DesignerChangeResult)!;
        Assert.Equal(2, back.Changes.Length);
        Assert.Null(back.Changes[1].Value);
        Assert.Equal((300, 200), (back.Render.Frame!.Width, back.Render.Frame.Height));

        // #78: the controls drawn again because they overlap; a frame from an older host, without it, reads as none.
        var restacked = JsonSerializer.Serialize(new DesignerRenderResult(new DesignerFrame("iVBORw0KGgo=", 300, 200, 2), [], []), json.DesignerRenderResult);
        Assert.Contains("\"restacked\":2", restacked, StringComparison.Ordinal);
        Assert.Equal(2, JsonSerializer.Deserialize(restacked, json.DesignerRenderResult)!.Frame!.Restacked);
        Assert.Equal(0, JsonSerializer.Deserialize("{\"frame\":{\"png\":\"iVBORw0KGgo=\",\"width\":300,\"height\":200},\"components\":[],\"errors\":[]}", json.DesignerRenderResult)!.Frame!.Restacked);
        Assert.Equal(31, back.Render.Components[0].ClientY);
        Assert.Equal("button1", back.Created!.Properties[0].Value.Value);
        Assert.Equal(["label1"], back.Removed);

        // M41b: z-order, collections with their locals, extenders, bindings, a localizable form's resources.
        var depth = new DesignerChangeResult(
            [new DesignerPropertyChange("button1", "ToolTip", DesignerValue.String("Saves")) { Provider = "toolTip1" }, new DesignerPropertyChange("textBox1", "Text", null) { DataBinding = true }],
            new DesignerRenderResult(null, [new DesignerComponentView("item1", "System.Windows.Forms.ToolStripMenuItem", null, 0, 0, 40, 20) { IsItem = true, Owner = "menuStrip1", ItemIndex = 2, Margin = [3, 3, 3, 3], Baseline = 15 }], []))
        {
            Reordered = [new DesignerChildOrder(string.Empty, ["button1", "label1"])],
            Collections = [new DesignerCollectionChange("listView1", "Items", "AddRange", "System.Windows.Forms.ListViewItem", [DesignerValue.ReferenceTo("listViewItem1")]) { Locals = [new DesignerLocal("listViewItem1", DesignerValue.NewOf("System.Windows.Forms.ListViewItem", DesignerValue.String("Hammer")))] }],
            Resources = [new DesignerResourceEntry("button1.Text", DesignerResource.String("Enregistrer")) { Culture = "fr" }],
            Localized = [new DesignerLocalized("button1", true)],
        };
        var depthBack = JsonSerializer.Deserialize(JsonSerializer.Serialize(depth, json.DesignerChangeResult), json.DesignerChangeResult)!;
        Assert.Equal(("toolTip1", true), (depthBack.Changes[0].Provider, depthBack.Changes[1].DataBinding));
        Assert.Equal(["button1", "label1"], depthBack.Reordered[0].Children);
        Assert.Equal("listViewItem1", depthBack.Collections[0].Locals[0].Name);
        Assert.Equal(("fr", "Enregistrer"), (depthBack.Resources[0].Culture, depthBack.Resources[0].Resource!.Text));
        Assert.True(depthBack.Localized[0].Localized);
        Assert.True(depthBack.Render.Components[0] is { IsItem: true, Owner: "menuStrip1", ItemIndex: 2, Baseline: 15 });
        var collection = JsonSerializer.Deserialize(JsonSerializer.Serialize(new DesignerSetCollectionParams("menuStrip1", "Items", [new DesignerCollectionItem { Name = "item1", Type = "System.Windows.Forms.ToolStripMenuItem", Text = "&Edit" }]), json.DesignerSetCollectionParams), json.DesignerSetCollectionParams)!;
        Assert.Equal("&Edit", collection.Items[0].Text);
        var actions = JsonSerializer.Deserialize(JsonSerializer.Serialize(new DesignerActionsResult([new DesignerActionItem(DesignerActionKinds.Method, "verb:Add Tab", "Add Tab") { Collection = "TabControl:TabPages" }]), json.DesignerActionsResult), json.DesignerActionsResult)!;
        Assert.Equal(("verb:Add Tab", "TabControl:TabPages"), (actions.Items[0].Member, actions.Items[0].Collection));

        // #66: the Toolbox's double-click or Enter (the selection's container, its default place), and a drop's defaults.
        var add = JsonSerializer.Serialize(new DesignerCreateParams("System.Windows.Forms.CheckBox", "checkBox1", null, 8, 31) { AtDefaultLocation = true, Selected = ["placeOrderButton", "buttonPanel"] }, json.DesignerCreateParams);
        Assert.Contains("\"atDefaultLocation\":true", add, StringComparison.Ordinal);
        Assert.Contains("\"selected\":[\"placeOrderButton\",\"buttonPanel\"]", add, StringComparison.Ordinal);
        var addBack = JsonSerializer.Deserialize(add, json.DesignerCreateParams)!;
        Assert.True(addBack is { AtDefaultLocation: true, X: 8, Y: 31, Parent: null });
        Assert.Equal(["placeOrderButton", "buttonPanel"], addBack.Selected);
        var drop = JsonSerializer.Deserialize(JsonSerializer.Serialize(new DesignerCreateParams("System.Windows.Forms.Button", "button1", "buttonPanel", 56, 331), json.DesignerCreateParams), json.DesignerCreateParams)!;
        Assert.False(drop.AtDefaultLocation);
        Assert.Empty(drop.Selected);
        Assert.Equal("buttonPanel", drop.Parent);
    }

    [Fact]
    public void Only_tool_strips_items_take_Type_Here()
    {
        // #67: a strip is a component whose Items hold tool strip items, by the collection table, not by type names.
        const string Forms = "System.Windows.Forms.";
        foreach (var strip in new[] { "MenuStrip", "ToolStrip", "StatusStrip", "ContextMenuStrip" })
        {
            Assert.True(DesignerCollections.IsStripItems(Forms + strip + ":Items"), strip);
        }

        foreach (var key in new[] { "ListBox:Items", "ComboBox:Items", "DomainUpDown:Items", "ListView:Items", "ListView:Columns", "ToolStripDropDownItem:DropDownItems", "TabControl:TabPages", "DataGridView:Columns" })
        {
            Assert.False(DesignerCollections.IsStripItems(Forms + key), key);
        }

        Assert.False(DesignerCollections.IsStripItems("Contoso.FilmStrip:Items"));
        Assert.False(DesignerCollections.IsStripItems("Items"));
        Assert.Equal(Forms + "ToolStripMenuItem", DesignerCollections.MenuItemType);
        Assert.Equal(DesignerCollections.MenuItemType, DesignerCollections.Find(Forms + "MenuStrip:Items")!.ItemTypes[0]);
    }

    [Fact]
    public async Task A_designer_host_that_isnt_there_fails_to_start_with_the_reason()
    {
        // A project folder that's gone: the process can't start, and the designer says why (no hang, no crash).
        var missing = Path.Combine(Path.GetTempPath(), "codalus-no-such-folder-" + Guid.NewGuid().ToString("N"));
        var target = new WinFormsDesignerTarget(Path.Combine(missing, "Form1.cs"), Path.Combine(missing, "App.csproj"), Path.Combine(missing, "App.dll"), null, Path.Combine(missing, "codalus-winformsdesigner.dll"));
        var error = await Assert.ThrowsAsync<PreviewerException>(() => WinFormsDesignerProcess.StartAsync(target, CancellationToken.None));
        Assert.StartsWith("The designer didn't start", error.Message, StringComparison.Ordinal);
    }

    private static readonly WinFormsDesignerTimeouts s_short = new() { Start = TimeSpan.FromSeconds(2), Load = TimeSpan.FromSeconds(1), Request = TimeSpan.FromSeconds(1), Status = TimeSpan.FromSeconds(2) };

    [Fact]
    public async Task A_load_that_runs_out_of_time_says_what_the_host_is_doing_and_what_it_wrote()
    {
        // A host that's ready but whose UI thread never finishes the load: the failure carries designer/status (answered
        // off the UI thread) and the host's last standard error, not just "didn't answer".
        await using var fake = new FakeDesignerHost(ready: true);
        var host = await WinFormsDesignerProcess.StartAsync(fake.Launch, s_short, TestContext.Current.CancellationToken);
        await using var _ = host;
        Assert.Equal(1, fake.ReadyRequests);
        var model = new DesignerModel { ClassName = "Form1", Namespace = "Demo", BaseType = "System.Windows.Forms.Form" };
        var error = await Assert.ThrowsAsync<PreviewerException>(() => host.LoadAsync(new DesignerLoadParams(model, "/app/App.dll", "Demo.Form1", []), TestContext.Current.CancellationToken));
        Assert.StartsWith("The designer didn't answer designer/load in 1 seconds: the host (process ", error.Message, StringComparison.Ordinal);
        Assert.Contains("says: UI thread: designer/load for 1.0 s (running line 12 of the designer file). This process waited ", error.Message, StringComparison.Ordinal);
        Assert.Contains(" pool threads, ", error.Message, StringComparison.Ordinal);
        Assert.Contains("; its connection: 0 request(s) awaiting a reply;", error.Message, StringComparison.Ordinal);
        Assert.Contains("Its last standard error:" + Environment.NewLine + "[designer] loading App.dll", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_limit_that_fires_late_says_this_process_was_too_busy_to_read_the_answer()
    {
        // Windows CI: a load's 60 s limit fired 34 s late, so the test's own thread pool was busy (every read of the
        // host's pipe is a work item on it there); the failure says so rather than only blaming the host.
        var late = WinFormsDesignerProcess.DescribeHere(TimeSpan.FromSeconds(94), TimeSpan.FromSeconds(60), 9, 412, "0 request(s) awaiting a reply; 1 write(s) pending");
        Assert.Equal("This process waited 94 s, its limit's timer 34 s late: its thread pool was too busy, so the host's answer may have waited unread (9 pool threads, 412 work items queued); its connection: 0 request(s) awaiting a reply; 1 write(s) pending.", late);
        Assert.Equal("This process waited 120 s (4 pool threads, 0 work items queued).", WinFormsDesignerProcess.DescribeHere(TimeSpan.FromSeconds(120.4), TimeSpan.FromSeconds(120), 4, 0, null));
    }

    [Fact]
    public async Task A_host_whose_message_loop_never_runs_fails_to_start_with_its_state_and_is_stopped()
    {
        // designer/ready never comes: the start fails after its own limit with what the host says (its window's message
        // loop hasn't started), and the process is ended rather than left behind.
        await using var fake = new FakeDesignerHost(ready: false);
        var error = await Assert.ThrowsAsync<PreviewerException>(() => WinFormsDesignerProcess.StartAsync(fake.Launch, s_short, TestContext.Current.CancellationToken));
        Assert.StartsWith("The designer didn't start in 2 seconds: the host (process ", error.Message, StringComparison.Ordinal);
        Assert.Contains("says: its window's message loop hasn't started.", error.Message, StringComparison.Ordinal);
        await fake.Stopped.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    /// <summary>An app's build output in a temp folder: its assembly, runtimeconfig and deps files.</summary>
    private static (string Folder, WinFormsDesignerTarget Target) AppOutput()
    {
        var folder = Directory.CreateTempSubdirectory("codalus-winforms-copy-").FullName;
        var output = Path.Combine(folder, "bin");
        Directory.CreateDirectory(output);
        var app = Path.Combine(output, "App.dll");
        File.WriteAllText(app, "app");
        File.WriteAllText(Path.Combine(output, "App.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(output, "App.deps.json"), "{}");
        return (folder, new WinFormsDesignerTarget(Path.Combine(folder, "Form1.cs"), Path.Combine(folder, "App.csproj"), app, app, "/ide/winformsdesigner/codalus-winformsdesigner.dll"));
    }

    private static async Task AssertNoCopyAsync(string root)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (Directory.Exists(root) && Directory.EnumerateDirectories(root).Any() && DateTime.UtcNow < until)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(!Directory.Exists(root) || !Directory.EnumerateDirectories(root).Any(), $"a copy was left in {root}");
    }

    [Fact]
    public async Task A_target_starts_on_the_copys_runtime_loads_the_copy_and_takes_the_copy_with_it()
    {
        // #66 on every OS: the host is launched on the copy's runtimeconfig, deps file and folder (never the build
        // output's, which would lock it again), designer/load names the copy's assemblies, and the copy goes with the host.
        var (folder, target) = AppOutput();
        var root = Path.Combine(folder, "copies");
        var output = Path.GetDirectoryName(target.ProjectAssembly)!;
        try
        {
            await using var fake = new FakeDesignerHost(ready: true, answerLoad: true);
            var host = (WinFormsDesignerProcess)await WinFormsDesignerProcess.StartAsync(target, s_short, TestContext.Current.CancellationToken, launcher: fake, copyRoot: root);
            var copy = host.CopyFolder!;
            var copied = Path.Combine(copy, "0");
            Assert.Equal(root, Path.GetDirectoryName(copy));
            Assert.True(File.Exists(Path.Combine(copied, "App.dll")));
            Assert.Equal(["exec", "--runtimeconfig", Path.Combine(copied, "App.runtimeconfig.json"), "--depsfile", Path.Combine(copied, "App.deps.json"), target.DesignerHost], fake.Started!.Arguments);
            Assert.Equal(copied, fake.Started.WorkingDirectory);
            Assert.DoesNotContain(fake.Started.Arguments, a => a.StartsWith(output, StringComparison.Ordinal));

            // The document sends the real paths; the host gets the copy's.
            var model = new DesignerModel { ClassName = "Form1", Namespace = "App", BaseType = "System.Windows.Forms.Form" };
            await host.LoadAsync(WinFormsDesignerService.LoadParams(target, model, "App.Form1"), TestContext.Current.CancellationToken);
            Assert.Equal(Path.Combine(copied, "App.dll"), fake.LastLoad!.ProjectAssembly);
            Assert.Equal([copied], fake.LastLoad.SearchFolders);
            Assert.False(await host.IsOutdatedAsync(TestContext.Current.CancellationToken));
            File.AppendAllText(target.ProjectAssembly, " rebuilt");
            Assert.True(await host.IsOutdatedAsync(TestContext.Current.CancellationToken)); // a build replaced the app's assembly

            await host.DisposeAsync();
            await AssertNoCopyAsync(root);

            // A library with no app: the host's own runtime, in the library's copy.
            var library = Path.Combine(folder, "lib");
            Directory.CreateDirectory(library);
            File.WriteAllText(Path.Combine(library, "Forms.dll"), "forms");
            await using var libraryFake = new FakeDesignerHost(ready: true, answerLoad: true);
            var libraryHost = (WinFormsDesignerProcess)await WinFormsDesignerProcess.StartAsync(target with { HostAssembly = null, ProjectAssembly = Path.Combine(library, "Forms.dll") }, s_short, TestContext.Current.CancellationToken, launcher: libraryFake, copyRoot: root);
            Assert.Equal([target.DesignerHost], libraryFake.Started!.Arguments);
            Assert.Equal(Path.Combine(libraryHost.CopyFolder!, "0"), libraryFake.Started.WorkingDirectory);
            await libraryHost.DisposeAsync();
            await AssertNoCopyAsync(root);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_host_process_that_cant_start_fails_with_the_reason_and_leaves_no_copy()
    {
        // The output is there (so the copy is made) but the process doesn't start: dotnet missing, or a pipe that
        // can't be made. The launcher's exception becomes the designer's reason, and the copy goes.
        var (folder, target) = AppOutput();
        var root = Path.Combine(folder, "copies");
        try
        {
            foreach (var failure in new Exception[] { new System.ComponentModel.Win32Exception(2, "An error occurred trying to start process 'dotnet'. The system cannot find the file specified."), new IOException("The pipe has been ended.") })
            {
                var launcher = new FailingLauncher(failure);
                var error = await Assert.ThrowsAsync<PreviewerException>(() => WinFormsDesignerProcess.StartAsync(target, s_short, TestContext.Current.CancellationToken, launcher: launcher, copyRoot: root));
                Assert.Equal("The designer didn't start: " + failure.Message, error.Message);
                Assert.Same(failure, error.InnerException);
                Assert.Equal(1, launcher.Starts);
                Assert.StartsWith(root, Path.GetDirectoryName(launcher.Started!.WorkingDirectory!)!, StringComparison.Ordinal); // it was started on the copy
                await AssertNoCopyAsync(root);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_output_stamp_changes_when_a_build_writes_or_replaces_the_assemblies()
    {
        // What a designer whose host didn't start compares when it becomes active (#66).
        var (folder, target) = AppOutput();
        try
        {
            Assert.Equal(string.Empty, WinFormsDesignerService.OutputStamp(null));
            var built = WinFormsDesignerService.OutputStamp(target);
            Assert.Equal(built, WinFormsDesignerService.OutputStamp(target));
            Assert.Single(built.Split(';', StringSplitOptions.RemoveEmptyEntries)); // the app is the designed project
            var missing = target with { ProjectAssembly = Path.Combine(folder, "bin", "Lib.dll") };
            var before = WinFormsDesignerService.OutputStamp(missing);
            Assert.Contains("Lib.dll|missing;", before, StringComparison.Ordinal);
            await File.WriteAllTextAsync(missing.ProjectAssembly, "lib", TestContext.Current.CancellationToken);
            Assert.NotEqual(before, WinFormsDesignerService.OutputStamp(missing));
            await File.AppendAllTextAsync(target.ProjectAssembly, " rebuilt", TestContext.Current.CancellationToken);
            Assert.NotEqual(built, WinFormsDesignerService.OutputStamp(target));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A launcher whose process doesn't start.</summary>
    private sealed class FailingLauncher(Exception failure) : IProcessLauncher
    {
        public int Starts { get; private set; }

        public ProcessLaunchInfo? Started { get; private set; }

        public Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
        {
            Starts++;
            Started = launch;
            return Task.FromException<IHostProcess>(failure);
        }
    }

    [Fact]
    public async Task A_host_that_never_becomes_ready_leaves_no_copy()
    {
        var (folder, target) = AppOutput();
        var root = Path.Combine(folder, "copies");
        try
        {
            await using var fake = new FakeDesignerHost(ready: false);
            var error = await Assert.ThrowsAsync<PreviewerException>(() => WinFormsDesignerProcess.StartAsync(target, s_short, TestContext.Current.CancellationToken, launcher: fake, copyRoot: root));
            Assert.StartsWith("The designer didn't start in 2 seconds", error.Message, StringComparison.Ordinal);
            Assert.NotNull(fake.Started);
            await AssertNoCopyAsync(root);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Early_log_lines_reach_the_first_subscriber()
    {
        // The document subscribes once the start returns: the copy's line and the host's ready line were written before.
        var (folder, target) = AppOutput();
        try
        {
            await using var fake = new FakeDesignerHost(ready: true);
            var host = await WinFormsDesignerProcess.StartAsync(target, s_short, TestContext.Current.CancellationToken, launcher: fake, copyRoot: Path.Combine(folder, "copies"));
            await using var _ = host;
            var lines = new List<string>();
            host.Log += (_, line) => lines.Add(line);
            Assert.Contains(lines, l => l.StartsWith("Copied the build output (3 files, ", StringComparison.Ordinal) && l.Contains("builds can replace the original while the designer runs.", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("The designer host is ready after ", StringComparison.Ordinal));
            var second = new List<string>();
            host.Log += (_, line) => second.Add(line);
            Assert.Empty(second); // replayed once, to the first
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Start_waits_for_a_running_build_before_checking_the_output()
    {
        // #66: a build the user started is running. The designer waits for it (its output may be half written, and a
        // second build can't start: "A build is already running."), then builds when the output is stale, then starts.
        var building = 1;
        var builds = 0;
        var starts = 0;
        var throwOnce = false;
        var progress = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var service = new WinFormsDesignerService(new SolutionService(() => "/nonexistent/codalus-buildhost.dll"), _ => true,
            (_, _) =>
            {
                Interlocked.Increment(ref builds);
                if (throwOnce)
                {
                    // A build started between the wait and this one.
                    throwOnce = false;
                    Volatile.Write(ref building, 1);
                    _ = Task.Delay(600, CancellationToken.None).ContinueWith(_ => Volatile.Write(ref building, 0), TaskScheduler.Default);
                    throw new InvalidOperationException("A build is already running.");
                }

                return Task.FromResult(true);
            },
            (_, _, _) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()))
        {
            IsWindows = () => true,
            IsBuilding = () => Volatile.Read(ref building) == 1,
            HostFactory = (_, _) =>
            {
                Interlocked.Increment(ref starts);
                throw new PreviewerException("started");
            },
        };
        var missing = Path.Combine(Path.GetTempPath(), "codalus-no-such-output-" + Guid.NewGuid().ToString("N"), "App.dll");
        var target = new WinFormsDesignerTarget(Path.Combine(Path.GetTempPath(), "Form1.cs"), Path.Combine(Path.GetTempPath(), "App.csproj"), missing, null, "/ide/winformsdesigner/codalus-winformsdesigner.dll");

        var start = service.StartAsync(target, progress.Enqueue, TestContext.Current.CancellationToken);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!progress.Contains("Waiting for the build to finish…") && DateTime.UtcNow < until)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Contains("Waiting for the build to finish…", progress);
        await Task.Delay(600, TestContext.Current.CancellationToken);
        Assert.False(start.IsCompleted);
        Assert.Equal((0, 0), (Volatile.Read(ref builds), Volatile.Read(ref starts)));
        Volatile.Write(ref building, 0);
        var started = await Assert.ThrowsAsync<PreviewerException>(() => start);
        Assert.Equal("started", started.Message);
        Assert.Equal((1, 1), (builds, starts)); // the output is missing: one build, then the host
        Assert.Equal(["Waiting for the build to finish…", "Building App for the designer…", "Starting the designer…"], progress);

        // A build that starts just before the designer's own: wait for it too, then build once more, and start.
        (builds, starts, throwOnce) = (0, 0, true);
        progress.Clear();
        started = await Assert.ThrowsAsync<PreviewerException>(() => service.StartAsync(target, progress.Enqueue, TestContext.Current.CancellationToken));
        Assert.Equal("started", started.Message);
        Assert.Equal((2, 1), (builds, starts));
        Assert.Equal(["Building App for the designer…", "Waiting for the build to finish…", "Building App for the designer…", "Starting the designer…"], progress);
    }

    /// <summary>
    /// A designer host in this process, over pipes: designer/ready answered (or never), designer/load never answered
    /// (or answered with an empty drawing), and designer/status saying what a stuck host would.
    /// </summary>
    private sealed class FakeDesignerHost : IProcessLauncher, IHostProcess
    {
        private readonly System.IO.Pipes.AnonymousPipeServerStream _toHost = new(System.IO.Pipes.PipeDirection.Out);
        private readonly System.IO.Pipes.AnonymousPipeServerStream _toIde = new(System.IO.Pipes.PipeDirection.In);
        private readonly System.IO.Pipes.AnonymousPipeClientStream _hostInput;
        private readonly System.IO.Pipes.AnonymousPipeClientStream _hostOutput;
        private readonly JsonRpcConnection _connection;
        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _ready;
        private int _readyRequests;

        public FakeDesignerHost(bool ready, bool answerLoad = false)
        {
            _ready = ready;
            _hostInput = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _toHost.ClientSafePipeHandle);
            _hostOutput = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.Out, _toIde.ClientSafePipeHandle);
            var json = WinFormsDesignerJsonContext.Default;
            _connection = new JsonRpcConnection(_hostInput, _hostOutput);
            _connection.OnRequest(WinFormsDesignerMethods.Ready, json.JsonElement, json.DesignerStatus, async (_, ct) =>
            {
                Interlocked.Increment(ref _readyRequests);
                if (!_ready)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return new DesignerStatus("message loop running for 0.1 s; UI thread: idle.");
            });
            _connection.OnRequest(WinFormsDesignerMethods.Status, json.JsonElement, json.DesignerStatus, (_, _) =>
                Task.FromResult(new DesignerStatus(_ready ? "UI thread: designer/load for 1.0 s (running line 12 of the designer file)." : "its window's message loop hasn't started.")));
            _connection.OnRequest(WinFormsDesignerMethods.Load, json.DesignerLoadParams, json.DesignerRenderResult, async (load, ct) =>
            {
                ErrorLine?.Invoke("[designer] loading App.dll");
                LastLoad = load;
                if (!answerLoad)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return new DesignerRenderResult(null, [], []);
            });
            _connection.OnRequest("shutdown", (_, _) => Task.FromResult<JsonElement?>(null));
        }

        public ProcessLaunchInfo Launch => new("codalus-winformsdesigner", []) { Launcher = this };

        public int ReadyRequests => Volatile.Read(ref _readyRequests);

        /// <summary>How the IDE started this host.</summary>
        public ProcessLaunchInfo? Started { get; private set; }

        /// <summary>The last designer/load's parameters.</summary>
        public DesignerLoadParams? LastLoad { get; private set; }

        /// <summary>Completes when the IDE ended the host (killed it, or it exited after shutdown).</summary>
        public Task Stopped => _exited.Task;

        public Stream Output => _toIde;

        public Stream Input => _toHost;

        public int? Id => 4242;

        public Task<int?> Exited => _exited.Task;

        public event Action<string>? ErrorLine;

        public Task<IHostProcess> StartAsync(ProcessLaunchInfo launch, CancellationToken cancellationToken)
        {
            Started = launch;
            _connection.Start();
            return Task.FromResult<IHostProcess>(this);
        }

        public void BeginErrorRead()
        {
        }

        public void Kill()
        {
            _exited.TrySetResult(-1);
            _hostOutput.Dispose();
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            _exited.TrySetResult(null);
            await _connection.DisposeAsync();
            _hostInput.Dispose();
            _hostOutput.Dispose();
            _toHost.Dispose();
            _toIde.Dispose();
        }

        public ValueTask DisposeAsync() => ((IAsyncDisposable)this).DisposeAsync();
    }
}
