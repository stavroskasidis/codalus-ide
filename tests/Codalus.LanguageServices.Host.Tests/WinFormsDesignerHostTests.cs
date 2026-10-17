using System.Diagnostics;
using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// samples/WindowsDesktop's Windows Forms app copied to a temp folder and built once for the test class, on Windows only
/// (D31: Windows Forms draws only there; elsewhere the tests skip and this does nothing).
/// </summary>
public sealed class WinFormsHostFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-winforms-" + Guid.NewGuid().ToString("N"));

    public string App => Path.Combine(_root, "WindowsDesktop", "WinFormsApp");

    public string AppDll { get; private set; } = string.Empty;

    /// <summary>The designer host built with the repository.</summary>
    public static string DesignerHost => Path.Combine(RepoPaths.Root, "src", "Codalus.WinForms.Designer", "bin", RepoPaths.Configuration, "net8.0-windows", WinFormsDesignerService.DesignerFileName);

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(File.Exists(DesignerHost), "Build the solution first: " + DesignerHost);
        RepoPaths.CopyFixture("WindowsDesktop", _root);
        var (code, output) = await DotnetAsync("build", Path.Combine(App, "WinFormsApp.csproj"), "-nologo", "-v", "quiet");
        Assert.True(code == 0, output);
        AppDll = Directory.EnumerateFiles(Path.Combine(App, "bin"), "WinFormsApp.dll", SearchOption.AllDirectories).Single();
    }

    public WinFormsDesignerTarget Target(string form) =>
        new(Path.Combine(App, form), Path.Combine(App, "WinFormsApp.csproj"), AppDll, AppDll, DesignerHost);

    /// <summary>A form's designer file read by the C# host's reader (as the designer document gets it).</summary>
    internal DesignerSource Read(string designerFile, string baseType = "System.Windows.Forms.Form") =>
        DesignerReader.Read(CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(App, designerFile))), baseType, DesignerReader.WindowsFormsImplicitUsings);

    public static async Task<(int Code, string Output)> DotnetAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// M41a (ADR 0073), Windows only: the Windows Forms designer's host on the fixture's own build output, through the same
/// process and protocol as the IDE: a VS-written form loaded on Windows Forms' own DesignSurface and drawn, its
/// components and their bounds, a Toolbox create, a property set, a move, a resize, a reparent and a delete coming back
/// as code that the C# host's writer turns into the expected minimal edits, the Properties window's descriptors and
/// events, a localizable form from its compiled resources, and the app's own user control.
/// </summary>
public sealed class WinFormsDesignerHostTests(WinFormsHostFixture fixture) : IClassFixture<WinFormsHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IWinFormsDesignerHost Host, DesignerRenderResult Render, DesignerSource Source)> LoadAsync(string form)
    {
        var source = fixture.Read(form[..^3] + ".Designer.cs", form.StartsWith("Controls", StringComparison.Ordinal) ? "System.Windows.Forms.UserControl" : "System.Windows.Forms.Form");
        var host = await WinFormsDesignerProcess.StartAsync(fixture.Target(form), Ct);
        var target = fixture.Target(form);
        var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, source.Model, source.Model.Namespace + "." + source.Model.ClassName), Ct);
        return (host, render, source);
    }

    private static string Apply(DesignerSource source, params DesignerEdit[] edits) =>
        new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), source.Model.BaseType, DesignerReader.WindowsFormsImplicitUsings)).Apply(source.Text, edits).ToString();

    private static List<DesignerEdit> Edits(DesignerChangeResult result) =>
    [
        .. result.Created is { } c ? [new DesignerEdit { Kind = DesignerEditKinds.AddComponent, Component = c.Name, Type = c.Type, CreationArguments = c.CreationArguments, Parent = c.IsControl ? c.Parent ?? string.Empty : null, ParentPath = c.ParentPath, Cell = c.Cell, Properties = c.Properties, SupportsInitialize = c.SupportsInitialize }] : Array.Empty<DesignerEdit>(),
        .. result.Reparented is { } r ? [new DesignerEdit { Kind = DesignerEditKinds.SetParent, Component = r.Component, Parent = r.Parent ?? string.Empty, ParentPath = r.ParentPath, Cell = r.Cell }] : Array.Empty<DesignerEdit>(),
        .. result.Removed.Select(n => new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = n }),
        .. result.Changes.Where(c => result.Created?.Name != c.Component).Select(c => new DesignerEdit { Kind = DesignerEditKinds.SetProperty, Component = c.Component, Property = c.Property, Value = c.Value }),
    ];

    [Fact]
    public async Task Loads_draws_and_lays_out_a_form_visual_studio_wrote()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, render, _) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
        Assert.NotNull(render.Frame);
        Assert.True(render.Frame.Width >= 384 && render.Frame.Height >= 401, $"{render.Frame.Width}×{render.Frame.Height}");
        var png = Convert.FromBase64String(render.Frame.Png);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);

        // The root first, its client area inside its border and caption; every control with its parent; the tool tip as a component.
        var root = render.Components[0];
        Assert.Equal(("OrderForm", "System.Windows.Forms.Form", (string?)null), (root.Name, root.Type, root.Parent));
        Assert.True(root.ClientX > 0 && root.ClientY > root.ClientX, $"client at {root.ClientX},{root.ClientY}");
        DesignerComponentView View(string name) => render.Components.Single(c => c.Name == name);
        Assert.Equal("buttonPanel", View("placeOrderButton").Parent);
        Assert.Equal("shippingGroupBox", View("expressRadioButton").Parent);
        Assert.Equal("detailsTable", View("orderDatePicker").Parent);
        Assert.Equal("notesSplit.Panel2", View("notesTextBox").Parent);
        Assert.True(View("notesSplit.Panel1").IsPart && View("notesSplit.Panel1").IsContainer);
        Assert.False(View("orderToolTip").IsControl);
        Assert.True(View("shippingGroupBox").IsContainer);
        Assert.False(View("customerLabel").IsContainer);
        Assert.Equal("Click", View("placeOrderButton").DefaultEvent);

        // Bounds in the frame: the label at its location in the client area; the panel docked at the bottom, full width.
        Assert.Equal(root.ClientX + 12, View("customerLabel").X, 1);
        Assert.Equal(root.ClientY + 15, View("customerLabel").Y, 1);
        Assert.Equal(384, View("buttonPanel").Width, 1);
        Assert.Equal(root.ClientY + 361, View("buttonPanel").Y, 1);
        var button = View("placeOrderButton");
        Assert.Equal(View("buttonPanel").ClientX + 196, button.X, 1);
        Assert.Equal((95d, 23d), (button.Width, button.Height));

        // Drawn again, the same.
        var again = await host.RenderAsync(Ct);
        Assert.Equal(render.Frame.Width, again.Frame!.Width);
    }

    [Fact]
    public async Task A_toolbox_create_and_property_edits_come_back_as_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, render, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        var root = render.Components[0];

        // A button dropped on the form at (290, 300) of its client area: VS's initial properties (Text is its name).
        var created = await host.CreateAsync(new DesignerCreateParams("System.Windows.Forms.Button", "button1", null, root.ClientX + 290, root.ClientY + 300), Ct);
        Assert.Null(created.Error);
        var button = created.Created!;
        Assert.Equal((string.Empty, (string?)null), (button.Parent, button.ParentPath));
        string Property(DesignerCreated c, string name) => c.Properties.Single(p => p.Name == name).Value is { Kind: DesignerValueKinds.New } v
            ? string.Join(", ", v.Arguments!.Select(a => a.Value)) : c.Properties.Single(p => p.Name == name).Value.Value!;
        Assert.Equal("button1", Property(button, "Text"));
        Assert.Equal("button1", Property(button, "Name"));
        Assert.Equal("290, 300", Property(button, "Location"));
        Assert.Equal("75, 23", Property(button, "Size"));
        Assert.Equal("true", Property(button, "UseVisualStyleBackColor"));
        Assert.Contains(created.Render.Components, c => c.Name == "button1" && c.Parent == "OrderForm");
        var withButton = Apply(source, [.. Edits(created)]);
        Assert.Contains("            button1.Location = new Point(290, 300);\n            button1.Name = \"button1\";\n            button1.Size = new Size(75, 23);", withButton, StringComparison.Ordinal);
        Assert.Contains("            Controls.Add(button1);\n            Controls.Add(counterControl);", withButton, StringComparison.Ordinal);

        // Text set in the Properties window: one line.
        var text = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("placeOrderButton", "Text") { Text = "&Order now" }]), Ct);
        var edits = Edits(text);
        var change = Assert.Single(edits);
        Assert.Equal(("placeOrderButton", "Text", "&Order now"), (change.Component, change.Property, change.Value!.Value));
        Assert.Contains("placeOrderButton.Text = \"&Order now\";", Apply(source, [.. edits]), StringComparison.Ordinal);

        // A move and a resize: Location and Size.
        var moved = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("giftCheckBox", "Location") { Text = "100, 72" }]), Ct);
        Assert.Contains(Edits(moved), e => e.Property == "Location" && e.Value!.Arguments![0].Value == "100" && e.Value.Arguments[1].Value == "72");
        var resized = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("notesSplit", "Size") { Text = "360, 120" }]), Ct);
        Assert.Contains(Edits(resized), e => e.Component == "notesSplit" && e.Property == "Size");

        // Docking a panel moves what it docks against: the change says so for every control it moved.
        var docked = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("buttonPanel", "Dock") { Text = "Top" }]), Ct);
        Assert.Contains(Edits(docked), e => e.Component == "buttonPanel" && e.Property == "Dock" && e.Value!.Members![0] == "Top");
        Assert.Contains(Edits(docked), e => e.Component == "buttonPanel" && e.Property == "Location");

        // An invalid value: an error, nothing changed.
        var invalid = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("giftCheckBox", "Location") { Text = "not a point" }]), Ct);
        Assert.NotNull(invalid.Error);
        Assert.Empty(invalid.Changes);
    }

    [Fact]
    public async Task Reparenting_and_deleting_come_back_as_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, render, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        var group = render.Components.Single(c => c.Name == "shippingGroupBox");

        var moved = await host.SetParentAsync(new DesignerSetParentParams("giftCheckBox", "shippingGroupBox", group.ClientX + 200, group.ClientY + 4), Ct);
        Assert.Equal(("giftCheckBox", "shippingGroupBox"), (moved.Reparented!.Component, moved.Reparented.Parent));
        var code = Apply(source, [.. Edits(moved)]);
        Assert.Contains("shippingGroupBox.Controls.Add(giftCheckBox);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("            Controls.Add(giftCheckBox);", code, StringComparison.Ordinal);
        Assert.Equal("shippingGroupBox", moved.Render.Components.Single(c => c.Name == "giftCheckBox").Parent);

        var deleted = await host.DeleteAsync(new DesignerComponentsParams(["buttonPanel"]), Ct);
        Assert.Equal(["buttonPanel", "cancelButton", "placeOrderButton"], deleted.Removed.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(deleted.Render.Components, c => c.Name == "placeOrderButton");
        var without = Apply(source, [.. Edits(deleted)]);
        Assert.DoesNotContain("placeOrderButton", without, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_properties_window_gets_descriptors_and_events()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, _, _) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        var properties = (await host.PropertiesAsync(new DesignerComponentsParams(["placeOrderButton"]), Ct)).Properties;
        // A bindable property (Text) also has its row in (DataBindings) since M41b: these are the properties' own rows.
        DesignerPropertyInfo Property(string name) => properties.Single(p => p.Name == name && !p.DataBinding);
        Assert.Equal(("placeOrderButton", true), (Property("Name").Value, Property("Name").IsSet));
        Assert.Equal(("&Place order", DesignerEditors.Text, true), (Property("Text").Value, Property("Text").Editor, Property("Text").IsSet));
        Assert.Equal(DesignerEditors.Anchor, Property("Anchor").Editor);
        Assert.Contains("Bottom", Property("Anchor").Value, StringComparison.Ordinal);
        Assert.Equal(DesignerEditors.Dock, Property("Dock").Editor);
        Assert.Contains("Fill", Property("Dock").StandardValues!);
        Assert.Equal((DesignerEditors.Color, "SteelBlue"), (Property("BackColor").Editor, Property("BackColor").Value));
        Assert.Equal(DesignerEditors.Font, Property("Font").Editor);
        Assert.Equal(DesignerEditors.Boolean, Property("Enabled").Editor);
        Assert.Equal("Appearance", Property("Text").Category);

        // Two buttons: what they share, their differing values mixed.
        var both = (await host.PropertiesAsync(new DesignerComponentsParams(["placeOrderButton", "cancelButton"]), Ct)).Properties;
        Assert.True(both.Single(p => p is { Name: "Text", DataBinding: false }).Mixed);
        Assert.True(both.Single(p => p.Name == "Size").Mixed);
        Assert.Equal(("Bottom, Right", false), (both.Single(p => p.Name == "Anchor").Value, both.Single(p => p.Name == "Anchor").Mixed));

        var events = await host.EventsAsync(new DesignerComponentsParams(["placeOrderButton"]), Ct);
        Assert.Equal("Click", events.DefaultEvent);
        var click = events.Events.Single(e => e.Name == "Click");
        Assert.Equal("System.EventHandler", click.DelegateType);
        Assert.Equal(["System.Object sender", "System.EventArgs e"], click.Parameters);
        Assert.Equal("System.Windows.Forms.MouseEventHandler", events.Events.Single(e => e.Name == "MouseDown").DelegateType);
        var load = await host.EventsAsync(new DesignerComponentsParams([string.Empty]), Ct);
        Assert.Equal("Load", load.DefaultEvent);
    }

    [Fact]
    public async Task A_localizable_form_and_the_apps_user_control_load()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (about, render, _) = await LoadAsync("AboutForm.cs");
        await using (about)
        {
            Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
            var ok = render.Components.Single(c => c.Name == "okButton");
            Assert.Equal((75d, 23d), (ok.Width, ok.Height)); // from AboutForm.resx through ApplyResources
            var text = (await about.PropertiesAsync(new DesignerComponentsParams(["okButton"]), Ct)).Properties.Single(p => p is { Name: "Text", DataBinding: false });
            Assert.Equal("OK", text.Value);
        }

        var (main, mainRender, _) = await LoadAsync("MainForm.cs");
        await using (main)
        {
            Assert.True(mainRender.Errors.Length == 0, string.Join("\n", mainRender.Errors));
            var counter = mainRender.Components.Single(c => c.Name == "counterControl");
            Assert.Equal(("WinFormsApp.Controls.CounterControl", 200d, 32d), (counter.Type, counter.Width, counter.Height));
            Assert.DoesNotContain(mainRender.Components, c => c.Name == "incrementButton"); // the control's own children aren't the form's
        }

        var (control, controlRender, _) = await LoadAsync(Path.Combine("Controls", "CounterControl.cs"));
        await using (control)
        {
            Assert.Equal("System.Windows.Forms.UserControl", controlRender.Components[0].Type);
            Assert.Contains(controlRender.Components, c => c.Name == "incrementButton");
        }
    }
}
