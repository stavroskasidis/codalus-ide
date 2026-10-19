using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Packages;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>NuGet packages, package sources, .NET tools and Pack (ADR 0012).</summary>
public sealed partial class ShellHost
{
    public PackageService Packages { get; private set; } = null!;

    private void InitializePackages()
    {
        Packages = new PackageService(Solutions) { Offline = Workbench.Settings.GetBoolean(PackageService.OfflineSetting, false) };
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Contains(PackageService.OfflineSetting))
            {
                Packages.Offline = Workbench.Settings.GetBoolean(PackageService.OfflineSetting, false);
            }
        };

        // Package documents belong to the solution that opened them.
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            if (!Solutions.IsOpen)
            {
                foreach (var document in Layout.Documents.Where(d => d is PackageManagerDocument or PackageSourcesDocument or DotnetToolsDocument).ToList())
                {
                    Layout.CloseDocument(document);
                }
            }
        });
    }

    private bool RequireTrustedSolution(string feature)
    {
        if (Solutions.Solution is not { } solution)
        {
            StatusBar.ShowMessage(Localizer.Format("msg.packages.openASolutionToUse", "Open a solution to use {0}.", feature), TimeSpan.FromSeconds(4));
            return false;
        }

        if (!solution.Trusted)
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.packages.isAvailableOnlyInTrustedFolders", "{0} is available only in trusted folders. Trust the solution folder first.", feature));
            return false;
        }

        return true;
    }

    /// <summary>Opens the NuGet Package Manager of a project (the current one when null), or of the solution.</summary>
    public PackageManagerViewModel? OpenPackageManager(string? project, bool solution = false)
    {
        if (!RequireTrustedSolution("the NuGet Package Manager"))
        {
            return null;
        }

        project = solution ? null : CurrentProject(project);
        if (!solution && project is null)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.packages.selectAProjectToManageIts", "Select a project to manage its packages."), TimeSpan.FromSeconds(4));
            return null;
        }

        if (Layout.FindDocument<PackageManagerDocument>(PackageManagerDocument.DocumentIdFor(project)) is { } open)
        {
            Layout.OpenDocument(open, focus: true);
            return open.ViewModel;
        }

        var context = new PackageManagerContext(
            Dialogs,
            Workbench.Notifications,
            url => ShellIntegration.OpenUrl(new Uri(url)),
            OpenPackageSources,
            () => Workbench.Output.Show(RestoreLog))
        {
            SignIn = SignInToPackageSourceAsync,
        };
        var viewModel = new PackageManagerViewModel(Packages, Solutions, project, context);
        Layout.OpenDocument(new PackageManagerDocument(viewModel), focus: true);
        return viewModel;
    }

    /// <summary>
    /// Signs in to a package source through NuGet's credential providers (deferred from M9a): the provider's device code
    /// shows in a notification that copies it and opens the page. <paramref name="source"/> null: pick one.
    /// </summary>
    public async Task<bool> SignInToPackageSourceAsync(string? source)
    {
        if (!RequireTrustedSolution("Package source sign-in"))
        {
            return false;
        }

        if (source is null)
        {
            var sources = (await Packages.SourcesAsync(CancellationToken.None).ConfigureAwait(true)).Sources
                .Where(s => s.Enabled && Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                .ToList();
            if (sources.Count == 0)
            {
                Workbench.Notifications.Info(Localizer.Get("msg.packages.noOnlineSources", "The solution has no online package sources to sign in to."));
                return false;
            }

            var items = sources.Select(s => new QuickPickItem(s.Name) { Tag = s.Name, Detail = s.Url }).ToList();
            if (await Palette.ShowAsync(items, Localizer.Get("msg.packages.signInWhichSource", "Sign in to which package source?")).ConfigureAwait(true) is not { Tag: string picked })
            {
                return false;
            }

            source = picked;
        }

        Codalus.Workbench.Notifications.Notification? code = null;
        void OnMessage(object? sender, PackageSignInMessage message) => Ui.Post(() =>
        {
            if (message.Source != source)
            {
                return;
            }

            RestoreLog.AppendLine($"[sign-in] {message.Source}: {message.Message}");
            if (DeviceCode(message.Message) is { } device)
            {
                if (code is not null)
                {
                    Workbench.Notifications.Dismiss(code);
                }

                var shown = code = Workbench.Notifications.Info(message.Message, Localizer.Get("accounts.copyCodeAndOpenBrowser", "Copy Code and Open Browser"));
                _ = shown.Result.ContinueWith(t =>
                {
                    if (t.Result is not null)
                    {
                        Ui.Post(() => _ = CopyCodeAndOpenAsync(device.Code, device.Url));
                    }
                }, TaskScheduler.Default);
            }
        });

        Packages.SignInMessage += OnMessage;
        StatusBar.ShowMessage(Localizer.Format("msg.packages.signingIn", "Signing in to {0}…", source), TimeSpan.FromSeconds(10));
        try
        {
            var result = await Packages.SignInAsync(source, CancellationToken.None).ConfigureAwait(true);
            if (result.SignedIn)
            {
                StatusBar.ShowMessage(result.Message, TimeSpan.FromSeconds(5));
            }
            else
            {
                Workbench.Notifications.Warning(result.Message);
            }

            return result.SignedIn;
        }
        catch (Exception ex) when (PackageManagerViewModel.IsExpected(ex))
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.packages.signInFailed", "Couldn't sign in to {0}: {1}", source, ex.Message));
            return false;
        }
        finally
        {
            Packages.SignInMessage -= OnMessage;
            if (code is not null)
            {
                Workbench.Notifications.Dismiss(code);
            }
        }
    }

    /// <summary>The page and code of a device-code sign-in message ("open the page https://… and enter the code ABCD-1234"), or null.</summary>
    internal static (string Url, string Code)? DeviceCode(string message) =>
        DeviceCodePattern().Match(message) is { Success: true } match ? (match.Groups["url"].Value.TrimEnd('.', ','), match.Groups["code"].Value) : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<url>https?://\S+).*?\bcode\s+(?<code>[A-Z0-9][A-Z0-9-]{3,})", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex DeviceCodePattern();

    private async Task CopyCodeAndOpenAsync(string code, string url)
    {
        if (Window?.Clipboard is { } clipboard)
        {
            await Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, code).ConfigureAwait(true);
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShellIntegration.OpenUrl(uri);
        }
    }

    public void OpenPackageSources()
    {
        if (!RequireTrustedSolution("Package Sources"))
        {
            return;
        }

        Layout.OpenDocument(
            Layout.FindDocument<PackageSourcesDocument>(PackageSourcesDocument.DocumentId)
            ?? new PackageSourcesDocument(new PackageSourcesViewModel(Packages, Workbench.Notifications)),
            focus: true);
    }

    public void OpenDotnetTools()
    {
        if (!RequireTrustedSolution(".NET tools"))
        {
            return;
        }

        Layout.OpenDocument(
            Layout.FindDocument<DotnetToolsDocument>(DotnetToolsDocument.DocumentId)
            ?? new DotnetToolsDocument(new DotnetToolsViewModel(Packages, Dialogs, path => OpenFileAsync(path))),
            focus: true);
    }

    /// <summary>
    /// <c>dotnet pack</c> of a project (the current one when null): the build output shows progress and diagnostics;
    /// a notification offers the created packages' folder.
    /// </summary>
    public async Task<IReadOnlyList<string>> PackAsync(string? argument)
    {
        if (CurrentProject(argument) is not { } project)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.packages.selectAProjectToPack", "Select a project to pack."), TimeSpan.FromSeconds(4));
            return [];
        }

        var created = new List<string>();
        void OnOutput(object? sender, string line)
        {
            if (PackOutput.CreatedPackage(line) is { } package)
            {
                lock (created)
                {
                    created.Add(package);
                }
            }
        }

        Solutions.BuildOutput += OnOutput;
        bool succeeded;
        try
        {
            succeeded = await BuildAsync(BuildTargets.Pack, project).ConfigureAwait(true);
        }
        finally
        {
            Solutions.BuildOutput -= OnOutput;
        }

        if (succeeded && created.Count > 0)
        {
            var folder = Path.GetDirectoryName(created[0])!;
            var notification = Workbench.Notifications.Info(
                $"Created {string.Join(", ", created.Select(Path.GetFileName))}.",
                OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Open Folder");
            _ = notification.Result.ContinueWith(
                t =>
                {
                    if (t.Result is not null)
                    {
                        ShellIntegration.RevealInFileManager(created[0]);
                    }
                },
                TaskScheduler.Default);
            Builds.Channel.AppendLine($"Packages: {folder}");
        }

        return created;
    }
}

/// <summary>Reads NuGet's pack output.</summary>
public static class PackOutput
{
    /// <summary>NuGet's "Successfully created package '&lt;path&gt;'." line (untranslated English output).</summary>
    public static string? CreatedPackage(string line)
    {
        const string Marker = "Successfully created package '";
        var start = line.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += Marker.Length;
        var end = line.IndexOf('\'', start);
        return end > start ? line[start..end] : null;
    }
}
