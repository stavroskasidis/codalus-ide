using System.Diagnostics;
using System.Drawing;
using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Codalus.Tests;
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
/// The Windows Forms app's one build for the designer host's test classes that only read it (#68: built once, not once a
/// class); each test starts its own host process.
/// </summary>
[CollectionDefinition(nameof(WinFormsHostBuild))]
public sealed class WinFormsHostBuild : ICollectionFixture<WinFormsHostFixture>;

/// <summary>
/// M41a (ADR 0073), Windows only: the Windows Forms designer's host on the fixture's own build output, through the same
/// process and protocol as the IDE: a VS-written form loaded on Windows Forms' own DesignSurface and drawn, its
/// components and their bounds, a Toolbox create, a property set, a move, a resize, a reparent and a delete coming back
/// as code that the C# host's writer turns into the expected minimal edits, the Properties window's descriptors and
/// events, a localizable form from its compiled resources, and the app's own user control.
/// </summary>
[Collection(nameof(WinFormsHostBuild))]
public sealed class WinFormsDesignerHostTests(WinFormsHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(IWinFormsDesignerHost Host, DesignerRenderResult Render, DesignerSource Source)> LoadAsync(string form)
    {
        var source = fixture.Read(form[..^3] + ".Designer.cs", form.StartsWith("Controls", StringComparison.Ordinal) ? "System.Windows.Forms.UserControl" : "System.Windows.Forms.Form");
        var host = await WinFormsDesignerProcess.StartAsync(fixture.Target(form), Ct);
        try
        {
            var target = fixture.Target(form);
            var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, source.Model, source.Model.Namespace + "." + source.Model.ClassName), Ct);
            return (host, render, source);
        }
        catch
        {
            // A load that failed (or ran out of time) ends its host: one left running would take the machine from the tests after it.
            await host.DisposeAsync();
            throw;
        }
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

        // Visible and Enabled are ControlDesigner's shadows, at the control's defaults: not written (Visual Studio doesn't).
        Assert.DoesNotContain(button.Properties, p => p.Name is "Visible" or "Enabled");
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

    /// <summary>
    /// #66: every Toolbox gesture creates as the classic IDE does, through the type's Toolbox item and the container
    /// designer's CreateTool. A double-click (or Enter) goes into the selection's container at its default place, a
    /// grid step below and right of a selected control (back to the corner when it would run off); a tab control hands
    /// it to its selected page; a label sizes itself to its text; a split container fills an empty panel; the first
    /// menu strip becomes the form's main menu; a timer goes to the tray; a drop on a table takes the cell under the
    /// point. The expected places come from the render (Windows Forms' own AddControl placed them), not typed numbers.
    /// </summary>
    [Fact]
    public async Task Toolbox_items_are_created_as_the_classic_ide_creates_them()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, render, source) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        var root = render.Components[0];
        DesignerCreateParams Add(string type, string name, params string[] selected) =>
            new("System.Windows.Forms." + type, name, null, root.ClientX, root.ClientY) { AtDefaultLocation = true, Selected = selected };
        static DesignerComponentView View(DesignerChangeResult result, string name) => result.Render.Components.Single(c => c.Name == name);
        static string? Value(DesignerCreated created, string property) => created.Properties.SingleOrDefault(p => p.Name == property)?.Value switch
        {
            null => null,
            { Kind: DesignerValueKinds.New } v => string.Join(", ", v.Arguments!.Select(a => a.Value)),
            { Members: { } members } => string.Join(", ", members),
            var v => v.Value,
        };

        async Task<DesignerChangeResult> CreateAsync(DesignerCreateParams parameters)
        {
            var result = await host.CreateAsync(parameters, Ct);
            Assert.True(result.Error is null, $"{parameters.Name}: {result.Error}");
            Assert.Equal(parameters.Name, result.Created?.Name);
            return result;
        }

        // The form selected: at its default place (the client area's top-left corner), at the default size.
        var first = await CreateAsync(Add("Button", "button1", "OrderForm"));
        Assert.Equal((string.Empty, (string?)null), (first.Created!.Parent, first.Created.ParentPath));
        var button1 = View(first, "button1");
        Assert.Equal((root.ClientX, root.ClientY, 75d, 23d), (button1.X, button1.Y, button1.Width, button1.Height));

        // A control selected: a grid step below and right of it, so repeated double-clicks cascade.
        var second = await CreateAsync(Add("Button", "button2", "button1"));
        var button2 = View(second, "button2");
        Assert.Equal(string.Empty, second.Created!.Parent);
        Assert.Equal((button1.X + 8, button1.Y + 8), (button2.X, button2.Y));

        // A control in a panel selected: into that panel, the step below and right, or the panel's corner when that would run off it.
        var check = await CreateAsync(Add("CheckBox", "checkBox1", "placeOrderButton"));
        Assert.Equal("buttonPanel", check.Created!.Parent);
        var panel = View(check, "buttonPanel");
        var place = View(check, "placeOrderButton");
        var box = View(check, "checkBox1");
        var cascaded = (X: place.X + 8, Y: place.Y + 8);
        var fits = cascaded.X + box.Width < panel.ClientX + panel.Width && cascaded.Y + box.Height < panel.ClientY + panel.Height;
        Assert.Equal(fits ? cascaded : (panel.ClientX, panel.ClientY), (box.X, box.Y));

        // A label sizes itself to its text, as the classic IDE's AutoSizeToolboxItem makes it.
        var label = await CreateAsync(Add("Label", "label1", "OrderForm"));
        Assert.Equal("true", Value(label.Created!, "AutoSize"));
        Assert.Contains("            label1.AutoSize = true;\n", Apply(source, [.. Edits(label)]), StringComparison.Ordinal);

        // A tab control selected: the button goes to its selected page.
        var tabs = await CreateAsync(Add("TabControl", "tabControl1", "OrderForm"));
        Assert.Contains(tabs.Added, a => a.Name == "tabPage1");
        var inTab = await CreateAsync(Add("Button", "button3", "tabControl1"));
        Assert.Equal("tabPage1", inTab.Created!.Parent);

        // A split container added to an empty panel fills it (AutoDock).
        await CreateAsync(Add("Panel", "panel1", "OrderForm"));
        var split = await CreateAsync(Add("SplitContainer", "splitContainer1", "panel1"));
        Assert.Equal("panel1", split.Created!.Parent);
        Assert.Equal("Fill", Value(split.Created, "Dock"));

        // The first menu strip becomes the form's main menu.
        var menu = await CreateAsync(Add("MenuStrip", "menuStrip1", "OrderForm"));
        Assert.Contains(menu.Changes, c => c.Component.Length == 0 && c.Property == "MainMenuStrip" && c.Value?.Value == "menuStrip1");
        Assert.Contains("            MainMenuStrip = menuStrip1;\n", Apply(source, [.. Edits(menu)]), StringComparison.Ordinal);

        // A timer isn't a control: the tray.
        var timer = await CreateAsync(Add("Timer", "timer1", "OrderForm"));
        Assert.False(timer.Created!.IsControl);
        Assert.Null(timer.Created.Parent);
        Assert.False(View(timer, "timer1").IsControl);

        // A drop on a table: the cell under the point (a new table is two by two).
        var table = View(await CreateAsync(Add("TableLayoutPanel", "tableLayoutPanel1", "OrderForm")), "tableLayoutPanel1");
        var dropped = await CreateAsync(new DesignerCreateParams("System.Windows.Forms.Button", "button4", "tableLayoutPanel1", table.ClientX + (table.Width * 3 / 4), table.ClientY + (table.Height * 3 / 4)));
        Assert.Equal("tableLayoutPanel1", dropped.Created!.Parent);
        Assert.Equal([1, 1], dropped.Created.Cell ?? []);
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
        // #68: ButtonBase.Text's UITypeEditor is the multi-line string editor; Reset is the descriptor's CanResetValue.
        Assert.Equal(("&Place order", DesignerEditors.Multiline, true), (Property("Text").Value, Property("Text").Editor, Property("Text").IsSet));
        Assert.True(Property("Text").CanReset);
        Assert.False(Property("Name").CanReset);
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

    /// <summary>
    /// #78: a button created over another control (at <paramref name="at"/> of <paramref name="parent"/>'s client area,
    /// the form's when null), then sent to the back and brought to the front again, looked at in
    /// <paramref name="region"/> (of the same client area), inside both controls. Control.DrawToBitmap draws a
    /// container's controls in reverse order ("Controls inside containers are rendered in reverse order"), so the front
    /// one was drawn behind: the drop showed the control under it, and Send to Back seemed to bring it to the front.
    /// Every check is collected (the red run on the old renderer shows both halves of the reversal).
    /// </summary>
    private static async Task<List<string>> OverlapChecksAsync(IWinFormsDesignerHost host, DesignerRenderResult render, string? parent, Point at, Rectangle region)
    {
        var container = parent is null ? render.Components[0] : render.Components.Single(c => c.Name == parent);
        var (x, y) = ((int)container.ClientX, (int)container.ClientY);
        var r = region with { X = x + region.X, Y = y + region.Y };
        var area = r.Width * r.Height;
        var checks = new List<string>();
        var frame0 = PngPixels.FromBase64(render.Frame!.Png);

        var created = await host.CreateAsync(new DesignerCreateParams("System.Windows.Forms.Button", "button1", parent, x + at.X, y + at.Y), Ct);
        if (created.Error is { } error)
        {
            return [$"the button wasn't created: {error}"];
        }

        // The host says what it drew again: the two overlapping controls, each once (and nothing in a form without overlaps).
        if (created.Render.Frame!.Restacked != 2)
        {
            checks.Add($"created, {created.Render.Frame.Restacked} controls were drawn again, not the 2 that overlap");
        }

        var frame1 = PngPixels.FromBase64(created.Render.Frame.Png);
        var inFront = frame1.Differing(frame0, r);
        if (inFront * 10 < area * 3)
        {
            checks.Add($"created (in front), the button isn't drawn over the control: {inFront} of {area} pixels of {r} changed, at least 30 % expected");
        }

        var siblings = created.Render.Components.Count(c => c.Parent == container.Name && !c.IsPart);
        var back = await host.SetChildIndexAsync(new DesignerChildIndexParams("button1", siblings - 1), Ct);
        if (back.Render.Frame!.Restacked != 2)
        {
            checks.Add($"sent to the back, {back.Render.Frame.Restacked} controls were drawn again, not 2");
        }

        var behind = PngPixels.FromBase64(back.Render.Frame!.Png).Differing(frame0, r);
        if (behind != 0)
        {
            checks.Add($"sent to the back, the control isn't drawn over the button: {behind} of {area} pixels of {r} differ from the frame before the button");
        }

        var front = await host.SetChildIndexAsync(new DesignerChildIndexParams("button1", 0), Ct);
        var again = PngPixels.FromBase64(front.Render.Frame!.Png).Differing(frame1, r);
        if (again != 0)
        {
            checks.Add($"brought to the front again, the button isn't drawn as when it was created: {again} of {area} pixels of {r} differ");
        }

        return checks;
    }

    [Fact]
    public async Task A_control_dropped_on_another_is_drawn_in_front_of_it_and_follows_its_order()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, render, _) = await LoadAsync("OrderForm.cs");
        await using var _ = host;
        var root = render.Components[0];
        var frame0 = PngPixels.FromBase64(render.Frame!.Png);
        var empty = frame0.At((int)root.ClientX + 380, (int)root.ClientY + 340);
        Assert.True(empty.ToArgb() == SystemColors.Control.ToArgb(), $"the form's empty space reads {empty}, not {SystemColors.Control}: the frame isn't read right");

        // On the counter (172, 295, 200×32): the region is inside the button (290, 300, 75×23) and the counter, right of
        // the counter's own "+1" button (which ends at x 317).
        var checks = await OverlapChecksAsync(host, render, null, new Point(290, 300), new Rectangle(318, 301, 47, 21));
        Assert.True(checks.Count == 0, string.Join("\n", checks));
    }

    [Fact]
    public async Task A_control_dropped_on_another_inside_a_container_is_drawn_in_its_order()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var (host, render, _) = await LoadAsync("OrderForm.cs");
        await using var _ = host;

        // In the group box, on expressRadioButton (110, 22, 63×19): the region is inside both, at any depth the same.
        var checks = await OverlapChecksAsync(host, render, "shippingGroupBox", new Point(130, 20), new Rectangle(131, 23, 41, 17));
        Assert.True(checks.Count == 0, string.Join("\n", checks));
    }

    /// <summary>
    /// #78: a form whose visible controls overlap nowhere (at any depth; InventoryForm's two tab pages share their
    /// bounds, but only the selected one is visible) has nothing drawn after the first pass, so its frame is exactly
    /// what it was before the redraw of overlapping controls.
    /// </summary>
    [Fact]
    public async Task Forms_without_overlapping_controls_are_drawn_once()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        foreach (var form in new[] { "OrderForm.cs", "InventoryForm.cs", "MainForm.cs", "AboutForm.cs" })
        {
            var (host, render, _) = await LoadAsync(form);
            await using (host)
            {
                Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
                Assert.True(render.Frame!.Restacked == 0, $"{form}: {render.Frame.Restacked} controls drawn again");
            }
        }
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
