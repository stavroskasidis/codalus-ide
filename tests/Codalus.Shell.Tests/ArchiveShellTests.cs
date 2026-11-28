using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Platform.Credentials;
using Codalus.Projects.Protocol;
using Codalus.Shell.Views;
using Codalus.Workbench.Mobile;
using Codalus.Workbench.Secrets;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Archive Manager (ADR 0114) in the headless shell on samples/Maui: Publish on the MAUI app opens it, its versions
/// come from the project, a second archive of the same version raises the build number in the project file, a signing
/// key is created and an archive distributed for Google Play. The build and the tools are stand-ins: nothing is built,
/// signed or uploaded.
/// </summary>
public sealed class ArchiveShellTests
{
    [AvaloniaFact]
    public async Task Publish_on_a_maui_app_archives_it_raises_its_build_number_and_distributes_it()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Maui");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Maui.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);
        var app = Path.Combine(folder, "TodoApp", "TodoApp.csproj");

        // The MAUI workload is "installed" (the run's check), and the archive's build writes what Android's publish does.
        Task<(int, string)> Run(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken ct) =>
            Task.FromResult<(int, string)>(string.Join(' ', arguments) == "workload list"
                ? (0, "Installed Workload Id      Manifest Version      Installation Source\n--------------------------------------------------------------------\nmaui                       10.0.1/10.0.100       SDK 10.0.100\n")
                : (1, string.Empty));
        f.Shell.UseMobileService(new MobileService(PlatformInfo.Current, new AndroidToolchain(PlatformInfo.Current, _ => null, f.UserData), Run, (_, _, _) => false, f.Shell.Workbench.Settings));
        var builds = new List<IReadOnlyDictionary<string, string>>();
        var tools = new List<ToolCommand>();
        var jdk = Path.Combine(f.UserData, "jdk");
        f.Shell.UseArchiveService(new ArchiveService(
            PlatformInfo.Current,
            new AppArchives(Path.Combine(f.UserData, "archives"), null),
            new SigningIdentities(Path.Combine(f.UserData, "signing.json"), new SecretStorage(new InMemoryCredentialStore())),
            () => new AndroidToolchain.Locations(Path.Combine(f.UserData, "sdk"), jdk, null, null),
            (command, _, _) =>
            {
                tools.Add(command);
                if (command.Arguments[0] == "-genkeypair")
                {
                    File.WriteAllText(command.Arguments[command.Arguments.ToList().IndexOf("-keystore") + 1], "keystore");
                }

                return Task.FromResult(0);
            },
            (target, project, properties, _) =>
            {
                Assert.Equal(BuildTargets.Publish, target);
                builds.Add(properties);
                Directory.CreateDirectory(properties["PublishDir"]);
                using (ZipFile.Open(Path.Combine(properties["PublishDir"], "dev.codalus.samples.todoapp-Signed.aab"), ZipArchiveMode.Create))
                {
                }

                File.WriteAllText(Path.Combine(properties["PublishDir"], "dev.codalus.samples.todoapp-Signed.apk"), "apk");
                return Task.FromResult(new BuildResultInfo(true, false, 0, 0, 1));
            },
            f.UserData,
            _ => null));

        Assert.Null(f.Shell.OpenPublish(app)); // not the folder publish page
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<ArchiveManagerView>().Any());
        var page = f.Shell.OpenArchiveManager(app)!;
        Assert.Equal("Archives: TodoApp", page.Title);
        await ShellFixture.PumpUntil(() => page.DisplayVersion == "1.0" && page.Version == "1" && page.ApplicationId == "dev.codalus.samples.todoapp", seconds: 60);
        Assert.Contains(page.Platforms, p => p.Value == MobilePlatform.Android);
        page.Platform = page.Platforms.Single(p => p.Value == MobilePlatform.Android);
        Assert.False(page.IncrementBuild);
        Assert.True(page.HasNoArchives);

        await page.ArchiveAsync();
        Assert.Equal("Archived TodoApp 1.0 (1) for Android.", page.Status);
        Assert.Equal(("net10.0-android", "aab;apk", "false"), (builds[0]["TargetFramework"], builds[0]["AndroidPackageFormats"], builds[0]["AndroidKeyStore"]));
        Assert.Single(page.Archives);
        Assert.True(page.IncrementBuild, "this version is archived: the next archive raises the build number");
        Assert.Equal("The build number becomes 2.", page.NextVersionNote);

        // The second archive writes the new build number into the project first.
        await page.ArchiveAsync();
        Assert.Equal("Archived TodoApp 1.0 (2) for Android.", page.Status);
        Assert.Contains("<ApplicationVersion>2</ApplicationVersion>", await File.ReadAllTextAsync(app, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(["1.0 (2)", "1.0 (1)"], page.Archives.Select(a => a.Version));

        // Distribute the newest for Google Play: a signing key is needed, then created, then it signs.
        Assert.Equal("1.0 (2)", page.SelectedArchive!.Version);
        page.Channel = page.Channels.Single(c => c.Value == DistributionChannel.GooglePlay);
        Assert.Equal("Choose a signing key, or create one.", page.DistributeProblem);
        page.KeystoreFolder = Path.Combine(f.UserData, "keys");
        page.KeystoreName = "todo";
        page.KeystorePassword = page.KeystoreConfirmPassword = "secret1";
        page.KeystoreOwner = "Jane Doe";
        await page.CreateKeystoreAsync();
        Assert.StartsWith("Created todo.keystore with the key upload.", page.SigningStatus, StringComparison.Ordinal);
        Assert.Equal("todo", page.Keystore!.Value.Name);
        Assert.Null(page.DistributeProblem);

        await page.DistributeAsync();
        Assert.True(page.HasDistribution, page.DistributeStatus);
        Assert.Contains("the Play Console", page.DistributeStatus, StringComparison.Ordinal);
        var sign = tools.Last();
        Assert.Equal(Path.Combine(jdk, "bin", OperatingSystem.IsWindows() ? "jarsigner.exe" : "jarsigner"), sign.Program);
        Assert.EndsWith("dev.codalus.samples.todoapp-2.aab", sign.Arguments[^2], StringComparison.Ordinal);
        Assert.DoesNotContain("secret1", sign.Arguments);
        Assert.Contains(f.Shell.Workbench.Notifications.Items, n => n.Message.StartsWith("TodoApp is ready for Google Play: ", StringComparison.Ordinal));
        Assert.NotNull(f.Shell.Workbench.Commands.Find("mobile.archiveManager"));
    }
}
