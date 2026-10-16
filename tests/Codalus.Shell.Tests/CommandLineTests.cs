using Avalonia.Headless.XUnit;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Tests;
using Codalus.Workbench.Plugins;

namespace Codalus.Shell.Tests;

/// <summary>
/// The release track's command line: <c>codalus file:line:column</c> in the running window (or a new one with
/// <c>--new-window</c>), <c>--list-plugins</c> and <c>--install-plugin</c> (from a gallery, a version of it, or a
/// package file; never from nuget.org in tests).
/// </summary>
public sealed class CommandLineTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-cli-" + Guid.NewGuid().ToString("N"));
    private readonly MockNuGetFeed _feed = new();
    private readonly HttpClient _http = new();

    private string PluginsDir => Path.Combine(_root, "plugins");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _feed.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_file_with_a_location_opens_in_the_editor_and_solutions_and_folders_open_as_before()
    {
        var file = Path.Combine(_root, "Program.cs");
        File.WriteAllText(file, "class C { }\n");
        var solution = Path.Combine(_root, "App.slnx");
        File.WriteAllText(solution, "<Solution />");

        var plain = ShellOptions.Parse([file]);
        Assert.Equal(file, plain.OpenFile);
        Assert.Null(plain.OpenAt);
        Assert.Null(plain.Path);

        var located = ShellOptions.Parse([file + ":12:5", "--new-window"]);
        Assert.Equal(file, located.OpenFile);
        Assert.Equal((11, 4), located.OpenAt);
        Assert.True(located.NewWindow);
        Assert.Equal((11, 0), ShellOptions.Parse([file + ":12"]).OpenAt);

        Assert.Equal(solution, ShellOptions.Parse([solution]).Path);
        Assert.Equal(_root, ShellOptions.Parse([_root]).Path);
        Assert.Equal("missing.cs:3", ShellOptions.Parse(["missing.cs:3"]).Path); // not a file: as before (an error once opened)
        Assert.Equal(file, ShellOptions.Parse([file, "--remote", "ssh:host"]).OpenFile); // the remote's path is after --remote

        var plugins = ShellOptions.Parse(["--install-plugin", "contoso.greeter@1.0.0", "--install-plugin", "x.nupkg", "--list-plugins", "--show-versions"]);
        Assert.Equal(["contoso.greeter@1.0.0", "x.nupkg"], plugins.InstallPlugins);
        Assert.True(plugins.ListPlugins && plugins.ShowVersions);
        Assert.Empty(plugins.Errors);
    }

    [Fact]
    public async Task Plugins_install_from_a_gallery_or_a_file_and_list()
    {
        _feed.AddPlugin("contoso.greeter", "1.0.0");
        _feed.AddPlugin("contoso.greeter", "1.1.0");
        _feed.AddPlugin("contoso.greeter", "2.0.0-preview.1");
        _feed.AddPlugin("contoso.notes", "3.0.0");
        var settings = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(settings, $$"""
            {
              // the feed, and no signatures: it's a local test feed
              "plugins.sources": ["{{_feed.Source}}"],
              "plugins.requireSignature": "none",
            }
            """, TestContext.Current.CancellationToken);
        var cli = new PluginCommandLine(PluginsDir, PlatformInfo.Current, new PluginGallery(_http)) { SettingsFile = settings };
        Assert.Empty(cli.List(versions: true));

        // The newest stable version; then an exact one replaces it at the next start.
        var output = new StringWriter();
        Assert.Equal(0, await cli.InstallAsync("contoso.greeter", output, TestContext.Current.CancellationToken));
        Assert.Contains("Installed contoso.greeter 1.1.0.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(["contoso.greeter@1.1.0"], cli.List(versions: true));
        Assert.Equal(_feed.Source, await File.ReadAllTextAsync(Path.Combine(PluginsDir, "contoso.greeter", PluginStore.SourceFile), TestContext.Current.CancellationToken));
        Assert.Equal(0, await cli.InstallAsync("contoso.greeter@1.0.0", output, TestContext.Current.CancellationToken));
        Assert.Contains("contoso.greeter 1.0.0 replaces the installed version when Codalus IDE next starts.", output.ToString(), StringComparison.Ordinal);

        // A package file.
        var package = await new PluginGallery(_http).DownloadAsync(_feed.Source, "contoso.notes", "3.0.0", Path.Combine(_root, "downloads"), TestContext.Current.CancellationToken);
        Assert.Equal(0, await cli.InstallAsync(package, output, TestContext.Current.CancellationToken));
        Assert.Equal(["contoso.greeter", "contoso.notes"], cli.List(versions: false));

        // What can't be installed says why, with exit code 1.
        Assert.Equal(1, await cli.InstallAsync("contoso.missing", output, TestContext.Current.CancellationToken));
        Assert.Contains("No plugin 'contoso.missing' was found on", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await cli.InstallAsync("contoso.greeter@9.9.9", output, TestContext.Current.CancellationToken));
        Assert.Contains("Version 9.9.9 of 'contoso.greeter' was not found", output.ToString(), StringComparison.Ordinal);

        // A source that requires signatures refuses the unsigned package.
        await File.WriteAllTextAsync(settings, $$"""{ "plugins.sources": ["{{_feed.Source}}"], "plugins.requireSignature": "all" }""", TestContext.Current.CancellationToken);
        var signed = new PluginCommandLine(Path.Combine(_root, "other"), PlatformInfo.Current, new PluginGallery(_http))
        {
            SettingsFile = settings,
            Verify = (_, _) => Task.FromResult(new PackageSignature(false, null, "the package isn't signed")),
        };
        Assert.Equal(1, await signed.InstallAsync("contoso.notes", output, TestContext.Current.CancellationToken));
        Assert.Contains("isn't signed", output.ToString(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task A_file_sent_to_the_running_window_opens_at_its_location()
    {
        await using var f = ShellFixture.Create();
        var file = Path.Combine(_root, "Notes.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\nthree four\n", TestContext.Current.CancellationToken);

        var pipe = SingleInstance.PipeName(f.Shell.Paths.State);
        var sent = Task.Run(() => SingleInstance.TrySend(pipe, new InstanceRequest("open", [file, "2", "6"])), TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => sent.IsCompleted && f.Shell.ActiveEditor is { Controller: not null } active && active.Model.Path == file, seconds: 30);
        Assert.True(await sent);
        var editor = f.Shell.ActiveEditor!.Controller!.Editor;
        var line = editor.Document.GetLineByOffset(editor.CaretOffset);
        Assert.Equal((3, 6), (line.LineNumber, editor.CaretOffset - line.Offset));
    }
}
