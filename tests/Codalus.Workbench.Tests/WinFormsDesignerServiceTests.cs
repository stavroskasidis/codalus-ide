using System.Text.Json;
using Codalus.LanguageServices.Protocol;
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
        Assert.Equal(31, back.Render.Components[0].ClientY);
        Assert.Equal("button1", back.Created!.Properties[0].Value.Value);
        Assert.Equal(["label1"], back.Removed);
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
}
