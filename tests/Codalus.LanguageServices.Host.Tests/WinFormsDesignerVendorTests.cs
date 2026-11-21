using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// samples/WinFormsPackages's <c>Contoso.WinForms.Board</c> (#461, ADR 0106): a control library whose package ships its own
/// designers for the classic IDE's designer server (a routing assembly and a design assembly under
/// <c>lib/net11.0-windows/Design/WinForms/Server</c>), built against Codalus's replacements of that server's API, packed
/// into the fixture's feed, and the app that uses it built once, on Windows only (D31; elsewhere this does nothing).
/// </summary>
public sealed class WinFormsVendorHostFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-winforms-vendor-" + Guid.NewGuid().ToString("N"));

    public string App => Path.Combine(_root, WinFormsPackages.Sample, "WinFormsVendorApp");

    public string AppDll { get; private set; } = string.Empty;

    /// <summary>The replacements' folder next to the designer host.</summary>
    public static string DesignTools => Path.Combine(Path.GetDirectoryName(WinFormsHostFixture.DesignerHost)!, "designtools");

    /// <summary>The app's packages' design folders, as the IDE reads them from its assets file.</summary>
    public IReadOnlyList<string> DesignFolders { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(File.Exists(WinFormsHostFixture.DesignerHost), "Build the solution first: " + WinFormsHostFixture.DesignerHost);
        var fixture = RepoPaths.CopyFixture(WinFormsPackages.Sample, _root);
        var (code, output) = await WinFormsHostFixture.DotnetAsync("build", Path.Combine(fixture, "src", "Contoso.WinForms.Board.Design.Routing", "Contoso.WinForms.Board.Design.Routing.csproj"), "-c", "Release", "-p:CodalusDesignTools=" + DesignTools, "-nologo", "-v", "quiet");
        Assert.True(code == 0, "dotnet build the design assemblies: " + output);
        (code, output) = await WinFormsHostFixture.DotnetAsync("pack", Path.Combine(fixture, "src", "Contoso.WinForms.Board", "Contoso.WinForms.Board.csproj"), "-o", Path.Combine(fixture, "feed"), "-nologo", "-v", "quiet");
        Assert.True(code == 0, "dotnet pack Contoso.WinForms.Board: " + output);
        (code, output) = await WinFormsHostFixture.DotnetAsync("build", Path.Combine(App, "WinFormsVendorApp.csproj"), "-nologo", "-v", "quiet");
        Assert.True(code == 0, output);
        AppDll = Directory.EnumerateFiles(Path.Combine(App, "bin"), "WinFormsVendorApp.dll", SearchOption.AllDirectories).Single();
        DesignFolders = WinFormsDesignFolders.Read(Path.Combine(App, "obj", "project.assets.json"));
    }

    public WinFormsDesignerTarget Target(bool vendorDesigners = true) =>
        new(Path.Combine(App, "BoardForm.cs"), Path.Combine(App, "WinFormsVendorApp.csproj"), AppDll, AppDll, WinFormsHostFixture.DesignerHost) { DesignFolders = vendorDesigners ? DesignFolders : [] };

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
/// #461, Windows only: a control library's own designers, built for the classic IDE's designer server, design its controls
/// in the designer host, through the same process and protocol as the IDE: the vendor's defaults when a control is
/// dropped, its smart tag and verbs, its property filter, a designer routed by name, a component's designer, a dialog it
/// shows (which closes itself here) and what it changed as code; a designer that throws, and the setting off, give the
/// base types' designers (#454).
/// </summary>
public sealed class WinFormsDesignerVendorHostTests(WinFormsVendorHostFixture fixture) : IClassFixture<WinFormsVendorHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_app_reads_its_packages_design_folder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var folder = Assert.Single(fixture.DesignFolders);
        Assert.EndsWith(Path.Combine("lib", "net11.0-windows", "Design", "WinForms", "Server"), folder, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(folder, "lib", "Contoso.WinForms.Board.Design.dll")));
    }

    [Fact]
    public async Task A_control_librarys_own_designers_design_its_controls()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var target = fixture.Target();
        await using var host = await WinFormsDesignerProcess.StartAsync(target, Ct);
        var log = new List<string>();
        host.Log += (_, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };

        var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, await ModelAsync(), "WinFormsVendorApp.BoardForm"), Ct);
        Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
        var root = render.Components[0];
        Assert.Equal(("board1", true), (render.Components.Single(c => c.Name == "board1").Name, render.Components.Single(c => c.Name == "board1").IsContainer));

        // The board's smart tag and verbs are its designer's.
        var board = await host.ActionsAsync(new DesignerComponentsParams(["board1"]), Ct);
        Assert.Contains(board.Items, i => i is { Kind: DesignerActionKinds.Header, DisplayName: "Board" });
        Assert.Contains(board.Items, i => i is { Kind: DesignerActionKinds.Property, Member: "Title", Value: "Sprint" });
        Assert.Contains(board.Items, i => i is { Kind: DesignerActionKinds.Method, Member: "AddColumn", DisplayName: "Add Column" });
        Assert.Contains(board.Verbs, v => v.DisplayName == "Board Settings...");

        // The routed card designer's smart tag.
        var card = await host.ActionsAsync(new DesignerComponentsParams(["card1"]), Ct);
        Assert.Contains(card.Items, i => i is { Kind: DesignerActionKinds.Text, DisplayName: "A card of the board" });

        // The vendor's property filter: the board has no BackgroundImage in the Properties window.
        var properties = await host.PropertiesAsync(new DesignerComponentsParams(["board1"]), Ct);
        Assert.DoesNotContain(properties.Properties, p => p.Name == "BackgroundImage");
        Assert.Contains(properties.Properties, p => p.Name == "Title");

        // Dropped from the Toolbox, a board gets the vendor's defaults: a title and a column the designer creates.
        var created = await host.CreateAsync(new DesignerCreateParams("Contoso.WinForms.Board.ContosoBoard", "board2", null, root.ClientX + 20, root.ClientY + 220), Ct);
        Assert.Null(created.Error);
        Assert.Contains(created.Created!.Properties, p => p.Name == "Title" && p.Value.Value == "New board");
        var firstColumn = Assert.Single(created.Added, a => a.Type == "Contoso.WinForms.Board.ContosoBoardColumn");
        var newBoard = Assert.Single(created.Collections, c => c is { Component: "board2", Property: "Columns" });
        Assert.Equal(("AddRange", "Contoso.WinForms.Board.ContosoBoardColumn"), (newBoard.Method, newBoard.ElementType));
        Assert.Equal([firstColumn.Name], newBoard.Items.Select(i => i.Value));

        // The smart tag's link runs the vendor's method, and the library's own collection says the new column after the code's (#461).
        var column = await host.InvokeActionAsync(new DesignerInvokeActionParams("board1", "AddColumn"), Ct);
        Assert.Null(column.Error);
        var added = Assert.Single(column.Added, a => a.Type == "Contoso.WinForms.Board.ContosoBoardColumn");
        var columns = Assert.Single(column.Collections, c => c is { Component: "board1", Property: "Columns" });
        Assert.Equal(["boardColumn1", added.Name], columns.Items.Select(i => i.Value));

        // Deleting the column takes it out of the collection too.
        var deleted = await host.DeleteAsync(new DesignerComponentsParams([added.Name]), Ct);
        Assert.Contains(added.Name, deleted.Removed);
        Assert.Equal(["boardColumn1"], Assert.Single(deleted.Collections, c => c is { Component: "board1", Property: "Columns" }).Items.Select(i => i.Value));

        // The vendor's dialog shows (it closes itself here), and what it changed comes back as code.
        var settings = Assert.Single(board.Verbs, v => v.DisplayName == "Board Settings...");
        var dialog = await host.InvokeActionAsync(new DesignerInvokeActionParams("board1", settings.Member), Ct);
        Assert.Null(dialog.Error);
        Assert.Contains(dialog.Changes, c => c is { Component: "board1", Property: "Title" } && c.Value!.Value == "From the dialog");

        // A dialog run as DevExpress's Grid Designer is: the designer only tells the classic IDE's process to run a modal
        // loop; the host asks the library's handler to continue, as that process would, and the dialog shows there.
        var big = Assert.Single(board.Verbs, v => v.DisplayName == "Board Designer...");
        var designed = await host.InvokeActionAsync(new DesignerInvokeActionParams("board1", big.Member), Ct);
        Assert.Null(designed.Error);
        Assert.Contains(designed.Changes, c => c is { Component: "board1", Property: "Title" } && c.Value!.Value == "From the board designer");

        // The panel whose designer throws is designed as a Panel, and the output says why.
        Assert.Contains(render.Components, c => c is { Name: "brokenPanel1", IsContainer: true });
        lock (log)
        {
            Assert.Contains(log, l => l.Contains("BoardDesigner", StringComparison.Ordinal) && l.Contains("is designed with", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("BrokenDesigner.Initialize failed", StringComparison.Ordinal) && l.Contains("designed as its base type", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("A control library's dialog is open: Board Settings", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("Asking the control library's designer to continue (UITypeEditorEndpoint/ContinueStartModalDialog)", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_click_goes_to_a_control_librarys_design_time_button_and_live_area()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var target = fixture.Target();
        await using var host = await WinFormsDesignerProcess.StartAsync(target, Ct);
        var log = new List<string>();
        host.Log += (_, line) =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };

        var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, await ModelAsync(), "WinFormsVendorApp.BoardForm"), Ct);
        Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
        var board = render.Components.Single(c => c.Name == "board1");
        var card = render.Components.Single(c => c.Name == "card1");

        // The library's designers take clicks (they have glyphs or a hit test); Windows Forms' own don't, here.
        Assert.True(board.TakesClicks);
        Assert.True(card.TakesClicks);
        Assert.False(render.Components[0].TakesClicks);
        Assert.False(render.Components.Single(c => c.Name == "brokenPanel1").TakesClicks);

        // The board's "Add column" button is a glyph while the board is the primary selection, as DevExpress's grid's
        // level designer is: not selected, the click goes nowhere and nothing changes.
        var button = new DesignerClickParams(board.X + board.Width - 50, board.Y + board.Height - 18);
        var unselected = await host.ClickAsync(button, Ct);
        Assert.False(unselected.Handled);
        Assert.Empty(unselected.Added);

        await host.SelectAsync(new DesignerComponentsParams(["board1"]), Ct);
        var clicked = await host.ClickAsync(button, Ct);
        Assert.True(clicked.Handled);
        var added = Assert.Single(clicked.Added, a => a.Type == "Contoso.WinForms.Board.ContosoBoardColumn");
        Assert.Contains(added.Properties, p => p.Name == "Caption" && p.Value.Value == "From the button");
        Assert.Equal(["boardColumn1", added.Name], Assert.Single(clicked.Collections, c => c is { Component: "board1", Property: "Columns" }).Items.Select(i => i.Value));

        // The board's empty area isn't its button: not handled.
        Assert.False((await host.ClickAsync(new DesignerClickParams(board.X + 150, board.Y + 100), Ct)).Handled);

        // The card's right half is live (its designer's hit test): the click goes to the card, whose menu shows where the
        // click was on the screen and pins it; the click answers once the menu closes, with what its item changed. The
        // card's left half isn't live.
        Assert.False((await host.ClickAsync(new DesignerClickParams(card.X + 2, card.Y + card.Height / 2), Ct)).Handled);
        var pinned = await host.ClickAsync(new DesignerClickParams(card.X + card.Width - 3, card.Y + card.Height / 2) { ScreenX = 200, ScreenY = 150 }, Ct);
        Assert.True(pinned.Handled);
        Assert.Contains(pinned.Changes, c => c is { Component: "card1", Property: "Pinned" } && c.Value!.Value == "true");

        lock (log)
        {
            Assert.Contains(log, l => l.Contains("goes to Contoso.WinForms.Board.Design.AddColumnGlyph", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("goes to card1", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Contains("A window the click opened is open, moved to 200,150", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_librarys_objects_that_arent_components_are_written_as_locals_and_read_back()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var target = fixture.Target();
        var code = await File.ReadAllTextAsync(Path.Combine(fixture.App, "BoardForm.Designer.cs"), Ct);
        await using var host = await WinFormsDesignerProcess.StartAsync(target, Ct);
        var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, Read(code).Model, "WinFormsVendorApp.BoardForm"), Ct);
        Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
        var verbs = (await host.ActionsAsync(new DesignerComponentsParams(["board1"]), Ct)).Verbs;
        async Task<DesignerChangeResult> Verb(string text)
        {
            var result = await host.InvokeActionAsync(new DesignerInvokeActionParams("board1", Assert.Single(verbs, v => v.DisplayName == text).Member), Ct);
            Assert.Null(result.Error);
            return result;
        }

        static string Lines(string text, string start, string end) =>
            string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').SkipWhile(l => !l.Contains(start, StringComparison.Ordinal)).TakeWhile(l => !l.Contains(end, StringComparison.Ordinal)).Select(l => l.Trim()));

        // The lane the code declares is read (an unrelated change leaves the lanes as they are); a new lane, an object
        // that isn't a component, is a local named as the classic IDE names it, its properties set before the board's
        // Lanes.Nodes.AddRange, which lists both.
        Assert.DoesNotContain((await Verb("Board Settings...")).Collections, c => c.Property == "Lanes.Nodes");
        var added = await Verb("Add Lane");
        var lanes = Assert.Single(added.Collections, c => c is { Component: "board1", Property: "Lanes.Nodes" });
        Assert.Equal(("AddRange", "Contoso.WinForms.Board.ContosoLane"), (lanes.Method, lanes.ElementType));
        Assert.Equal(["contosoLane1", "contosoLane2"], lanes.Items.Select(i => i.Value));
        Assert.Equal(["contosoLane1", "contosoLane2"], lanes.Locals.Select(l => l.Name));
        code = Apply(code, added);
        Assert.Equal(
            """
            contosoLane1.Column = boardColumn1;
            contosoLane1.Title = "Doing";
            contosoLane2.Column = boardColumn1;
            contosoLane2.Title = "Lane 2";
            board1.Lanes.Nodes.AddRange(new Contoso.WinForms.Board.ContosoLane[] { contosoLane1, contosoLane2 });
            """.ReplaceLineEndings("\n"),
            Lines(code, "contosoLane1.Column", "board1.Location"));
        Assert.Contains("Contoso.WinForms.Board.ContosoLane contosoLane2 = new Contoso.WinForms.Board.ContosoLane();", code, StringComparison.Ordinal);

        // A lane in a lane: its own collection, the child set up before its parent's Nodes.AddRange.
        code = Apply(code, await Verb("Add Sub-lane"));
        Assert.Equal(
            """
            contosoLane3.Title = "Sub-lane";
            contosoLane1.Column = boardColumn1;
            contosoLane1.Nodes.AddRange(new Contoso.WinForms.Board.ContosoLane[] { contosoLane3 });
            contosoLane1.Title = "Doing";
            contosoLane2.Column = boardColumn1;
            contosoLane2.Title = "Lane 2";
            board1.Lanes.Nodes.AddRange(new Contoso.WinForms.Board.ContosoLane[] { contosoLane1, contosoLane2 });
            """.ReplaceLineEndings("\n"),
            Lines(code, "contosoLane3.Title", "board1.Location"));

        // The code written loads in a new host, with no change to write until one is made.
        await using (var again = await WinFormsDesignerProcess.StartAsync(target, Ct))
        {
            var reloaded = await again.LoadAsync(WinFormsDesignerService.LoadParams(target, Read(code).Model, "WinFormsVendorApp.BoardForm"), Ct);
            Assert.True(reloaded.Errors.Length == 0, string.Join("\n", reloaded.Errors));
            var settings = Assert.Single((await again.ActionsAsync(new DesignerComponentsParams(["board1"]), Ct)).Verbs, v => v.DisplayName == "Board Settings...");
            Assert.DoesNotContain((await again.InvokeActionAsync(new DesignerInvokeActionParams("board1", settings.Member), Ct)).Collections, c => c.Property == "Lanes.Nodes");
        }

        // The lanes removed: their locals and statements go.
        var cleared = await Verb("Remove Lanes");
        Assert.Contains("contosoLane3", cleared.Removed);
        code = Apply(code, cleared);
        Assert.DoesNotContain("contosoLane", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Lanes.Nodes", code, StringComparison.Ordinal);
    }

    private static DesignerSource Read(string code) =>
        DesignerReader.Read(CSharpSyntaxTree.ParseText(code), "System.Windows.Forms.Form", DesignerReader.WindowsFormsImplicitUsings, new BoardTypes());

    /// <summary>A change's collections and removals written as the designer document writes them (WinFormsDesignerDocument.EditsFor).</summary>
    private static string Apply(string code, DesignerChangeResult result)
    {
        var source = Read(code);
        var edits = new List<DesignerEdit>();
        var created = new HashSet<string>(StringComparer.Ordinal);
        bool Known(string name) => created.Contains(name) || source.Model.Components.Any(c => c.Name == name);
        foreach (var collection in result.Collections)
        {
            foreach (var local in collection.Locals)
            {
                if (Known(local.Name))
                {
                    edits.Add(new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = local.Name });
                }

                edits.Add(new DesignerEdit { Kind = DesignerEditKinds.AddLocal, Component = local.Name, Value = local.Value });
                created.Add(local.Name);
            }

            edits.Add(new DesignerEdit
            {
                Kind = DesignerEditKinds.SetCollection, Component = collection.Component, Property = collection.Property, Method = collection.Method, Type = collection.ElementType,
                Items = collection.Items, Statements = collection.Statements.Length == 0 ? null : collection.Statements,
            });
        }

        edits.AddRange(result.Removed.Where(n => Known(n) && !created.Contains(n)).Select(n => new DesignerEdit { Kind = DesignerEditKinds.RemoveComponent, Component = n }));
        var writer = new DesignerWriter(t => DesignerReader.Read(CSharpSyntaxTree.ParseText(t), "System.Windows.Forms.Form", DesignerReader.WindowsFormsImplicitUsings, new BoardTypes()));
        return writer.Apply(Microsoft.CodeAnalysis.Text.SourceText.From(code), edits).ToString();
    }

    [Fact]
    public async Task With_the_setting_off_a_control_librarys_controls_are_designed_as_their_base_types()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var target = fixture.Target(vendorDesigners: false);
        await using var host = await WinFormsDesignerProcess.StartAsync(target, Ct);
        var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, await ModelAsync(), "WinFormsVendorApp.BoardForm"), Ct);
        Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
        var board = await host.ActionsAsync(new DesignerComponentsParams(["board1"]), Ct);
        Assert.DoesNotContain(board.Items, i => i.Member == "AddColumn");
        Assert.DoesNotContain(board.Verbs, v => v.DisplayName == "Board Settings...");
        var properties = await host.PropertiesAsync(new DesignerComponentsParams(["board1"]), Ct);
        Assert.Contains(properties.Properties, p => p.Name == "BackgroundImage");
        Assert.DoesNotContain(render.Components, c => c.TakesClicks);
    }

    [Fact]
    public async Task The_check_finds_nothing_the_libraries_design_assemblies_need_that_the_replacements_lack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var folder = Assert.Single(fixture.DesignFolders);
        var (code, output) = await WinFormsHostFixture.DotnetAsync("exec", "--runtimeconfig", Path.ChangeExtension(fixture.AppDll, ".runtimeconfig.json"), WinFormsHostFixture.DesignerHost,
            "--check-vendor-designers", "--search", Path.GetDirectoryName(fixture.AppDll)!, folder);
        Assert.True(code == 0, output);
        Assert.Contains("2 design assemblies checked: 0 gap(s).", output, StringComparison.Ordinal);
    }

    private async Task<DesignerModel> ModelAsync()
    {
        var text = await File.ReadAllTextAsync(Path.Combine(fixture.App, "BoardForm.Designer.cs"), Ct);
        return DesignerReader.Read(CSharpSyntaxTree.ParseText(text, cancellationToken: Ct), "System.Windows.Forms.Form", DesignerReader.WindowsFormsImplicitUsings, new BoardTypes()).Model;
    }

    /// <summary>The library's types as the compiler names them (the C# host's compilation, in the IDE); the rest as the syntactic reader does.</summary>
    private sealed class BoardTypes : IDesignerTypeResolver
    {
        private readonly SyntacticDesignerTypeResolver _syntactic = new("WinFormsVendorApp");

        public string? FullName(Microsoft.CodeAnalysis.SyntaxNode node) =>
            node.ToString() is var text && text.StartsWith("Contoso.", StringComparison.Ordinal) ? text : _syntactic.FullName(node);

        public bool? IsEnum(string fullName) => _syntactic.IsEnum(fullName);
    }
}
