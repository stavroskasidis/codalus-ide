using Codalus.LanguageServices.Host.WinForms;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.WinForms;
using Microsoft.CodeAnalysis.CSharp;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// samples/WinFormsPackages (#454): a control library's two packages (<c>Contoso.WinForms.Controls</c>, with the base
/// form and controls, and <c>Contoso.WinForms.Grid</c>, which depends on it), packed into the fixture's local feed
/// before the app that references them restores, so nothing comes from nuget.org or the machine's package cache.
/// </summary>
internal static class WinFormsPackages
{
    public const string Sample = "WinFormsPackages";

    /// <summary>Packs the library's packages into <c>feed/</c> of the fixture's copy at <paramref name="fixture"/>.</summary>
    public static async Task PackAsync(string fixture)
    {
        foreach (var library in new[] { "Contoso.WinForms.Controls", "Contoso.WinForms.Grid" })
        {
            var (code, output) = await WinFormsHostFixture.DotnetAsync("pack", Path.Combine(fixture, "src", library, library + ".csproj"), "-o", Path.Combine(fixture, "feed"), "-nologo", "-v", "quiet");
            Assert.True(code == 0, $"dotnet pack {library}: {output}");
        }
    }
}

/// <summary>The fixture packed, and its app built once for the test class, on Windows only (D31; elsewhere this does nothing).</summary>
public sealed class WinFormsPackageHostFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-winforms-packages-" + Guid.NewGuid().ToString("N"));

    public string App => Path.Combine(_root, WinFormsPackages.Sample, "WinFormsPackageApp");

    public string AppDll { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(File.Exists(WinFormsHostFixture.DesignerHost), "Build the solution first: " + WinFormsHostFixture.DesignerHost);
        var fixture = RepoPaths.CopyFixture(WinFormsPackages.Sample, _root);
        await WinFormsPackages.PackAsync(fixture);
        var (code, output) = await WinFormsHostFixture.DotnetAsync("build", Path.Combine(App, "WinFormsPackageApp.csproj"), "-nologo", "-v", "quiet");
        Assert.True(code == 0, output);
        AppDll = Directory.EnumerateFiles(Path.Combine(App, "bin"), "WinFormsPackageApp.dll", SearchOption.AllDirectories).Single();
    }

    public WinFormsDesignerTarget Target(string form) =>
        new(Path.Combine(App, form), Path.Combine(App, "WinFormsPackageApp.csproj"), AppDll, AppDll, WinFormsHostFixture.DesignerHost);

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
/// #454, Windows only: a form whose base type comes from a control library's package (as a vendor's template's form
/// derives from the vendor's form) loads in the designer host, through the same process and protocol as the IDE, on the
/// app's own build: the base type and the package's controls are found in the app's output although nothing loaded their
/// assemblies, the library's designers that aren't there give way to the base types' (Form's, Button's), and a control
/// of a package the app's code doesn't use yet is created from the Toolbox.
/// </summary>
public sealed class WinFormsDesignerPackageHostTests(WinFormsPackageHostFixture fixture) : IClassFixture<WinFormsPackageHostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_form_whose_base_type_comes_from_a_package_loads_and_takes_the_packages_controls()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows Forms designer runs on Windows only (D31).");
        var text = await File.ReadAllTextAsync(Path.Combine(fixture.App, "MainForm.Designer.cs"), Ct);
        var source = DesignerReader.Read(CSharpSyntaxTree.ParseText(text, cancellationToken: Ct), "Contoso.WinForms.ContosoForm", DesignerReader.WindowsFormsImplicitUsings, new PackageTypes());
        Assert.Equal("Contoso.WinForms.ContosoForm", source.Model.BaseType);
        var target = fixture.Target("MainForm.cs");
        await using var host = await WinFormsDesignerProcess.StartAsync(target, Ct);

        // The form loads, with the package's base type as its root and the package's button on it.
        var render = await host.LoadAsync(WinFormsDesignerService.LoadParams(target, source.Model, "WinFormsPackageApp.MainForm"), Ct);
        Assert.True(render.Errors.Length == 0, string.Join("\n", render.Errors));
        Assert.NotNull(render.Frame);
        var root = render.Components[0];
        Assert.Equal(("MainForm", true), (root.Name, root.IsContainer));
        var button = Assert.Single(render.Components, c => c.Name == "saveButton");
        Assert.Equal(("Contoso.WinForms.ContosoButton", "MainForm", true), (button.Type, button.Parent, button.IsControl));
        Assert.Equal((94d, 29d), (button.Width, button.Height));

        // The base form's own property, set in the Properties window, comes back as code (the root's, which the code names by nothing).
        var skin = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit("MainForm", "Skin") { Text = "Light" }]), Ct);
        Assert.Null(skin.Error);
        var change = Assert.Single(skin.Changes, c => c.Property == "Skin");
        Assert.Equal((string.Empty, "Light"), (change.Component, change.Value!.Value));

        // A control of the other package, which the app's code doesn't use (its assembly isn't loaded or referenced), from the Toolbox.
        var grid = await host.CreateAsync(new DesignerCreateParams("Contoso.WinForms.ContosoGrid", "contosoGrid1", null, root.ClientX + 120, root.ClientY + 90), Ct);
        Assert.Null(grid.Error);
        Assert.Equal(("contosoGrid1", "Contoso.WinForms.ContosoGrid", true), (grid.Created!.Name, grid.Created.Type, grid.Created.IsControl));
        Assert.Contains(grid.Render.Components, c => c.Name == "contosoGrid1" && c.Parent == "MainForm" && c.Width == 240 && c.Height == 150);
    }

    /// <summary>The package's types as the compiler names them (the C# host's compilation, in the IDE); the rest as the syntactic reader does.</summary>
    private sealed class PackageTypes : IDesignerTypeResolver
    {
        private readonly SyntacticDesignerTypeResolver _syntactic = new("WinFormsPackageApp");

        public string? FullName(Microsoft.CodeAnalysis.SyntaxNode node) =>
            node.ToString() is var text && text.StartsWith("Contoso.", StringComparison.Ordinal) ? text : _syntactic.FullName(node);

        public bool? IsEnum(string fullName) => _syntactic.IsEnum(fullName);
    }
}

/// <summary>The fixture packed and opened in the real build and language hosts, on Windows only (elsewhere it starts nothing).</summary>
public sealed class WinFormsPackageLanguageFixture : LanguageFixture
{
    protected override bool Enabled => OperatingSystem.IsWindows();

    protected override (string Sample, string Solution, int Projects) Sample => (WinFormsPackages.Sample, "WinFormsPackages.slnx", 1);

    protected override void Prepare() => WinFormsPackages.PackAsync(Fixture).GetAwaiter().GetResult();

    /// <summary>The design-time build after the restore: the packages' assemblies on the compiler's command line.</summary>
    protected override async Task ReadyAsync()
    {
        try
        {
            await WaitUntilAsync(() => Solutions.CompilerInputs.Count(i => i.Value.Arguments.Any(a => a.Contains("Contoso.WinForms.Grid.dll", StringComparison.OrdinalIgnoreCase))) == 1
                                       && Languages.Status("csharp") is { State: WorkspaceStates.Ready, Projects: 1 });
        }
        catch (Xunit.Sdk.XunitException)
        {
            Assert.Fail("The app's design-time build has no package references: restore " + RestoreState + "; "
                + string.Join("; ", Solutions.CompilerInputs.Select(i => $"{Path.GetFileName(i.Key)} {i.Value.State} {i.Value.Error}")) + "\n" + string.Join('\n', Log.TakeLast(40)));
        }
    }
}

/// <summary>
/// #454, Windows only (the fixture's packages build there without nuget.org): the Toolbox lists the components of the
/// project's referenced packages, a package's dependency's too, grouped by package as the classic IDE does, through the
/// real design-time build and C# host; what isn't a Toolbox item (an inherited <c>[ToolboxItem(false)]</c> among them)
/// and the framework's own assemblies aren't listed.
/// </summary>
public sealed class WinFormsToolboxPackageTests(WinFormsPackageLanguageFixture packages) : IClassFixture<WinFormsPackageLanguageFixture>
{
    [Fact]
    public async Task The_toolbox_lists_the_referenced_packages_controls_by_package()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The fixture's packages are packed and restored on Windows only.");
        var toolbox = await packages.Client.WinFormsToolboxAsync(packages.File("WinFormsPackageApp", "WinFormsPackageApp.csproj"), TestContext.Current.CancellationToken);
        Assert.Equal(
            [
                ("Contoso.WinForms.Controls", "Contoso.WinForms.ContosoButton", true, "A button in the form's skin."),
                ("Contoso.WinForms.Controls", "Contoso.WinForms.ContosoClock", false, "Ticks while the form is open."),
                ("Contoso.WinForms.Controls", "Contoso.WinForms.ContosoTextEdit", true, null),
                ("Contoso.WinForms.Grid", "Contoso.WinForms.ContosoGrid", true, "Shows rows and columns."),
            ],
            toolbox!.Items.Select(i => (i.Project, i.Type, i.IsControl, i.Description)));
    }
}
