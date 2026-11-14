using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Feedback;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>One line of the About window's details: a label and its value.</summary>
public sealed record AboutDetail(string Label, string Value);

/// <summary>
/// Help → About Codalus IDE: the product, its version, what it runs on (the .NET runtime, the OS and its architecture),
/// the license and links to the website, the source and the third-party notices. Copy Info puts the version and the
/// details on the clipboard, for a bug report or a forum post; Close (OK, Enter or Escape) closes the window.
/// </summary>
public sealed class AboutViewModel
{
    public const string WebsiteUrl = "https://codalus.dev/";

    public const string RepositoryUrl = "https://github.com/" + IssueReport.Repository;

    public const string LicenseUrl = RepositoryUrl + "/blob/master/LICENSE";

    public const string NoticesUrl = RepositoryUrl + "/blob/master/THIRD-PARTY-NOTICES.md";

    private readonly Func<string, Task> _copy;
    private readonly Action<Uri> _open;

    public AboutViewModel(IssueEnvironment environment, Func<string, Task> copy, Action<Uri> open)
    {
        _copy = copy;
        _open = open;
        Version = Localizer.Format("msg.about.version", "Version {0}", environment.Version);
        Details =
        [
            new(Localizer.Get("msg.about.runtime", ".NET runtime"), environment.Runtime),
            new(Localizer.Get("msg.about.os", "Operating system"), environment.OsDescription),
            new(Localizer.Get("msg.about.architecture", "Architecture"), environment.Architecture),
        ];
        InfoText = string.Join('\n', [$"{ProductName} {environment.Version}", .. Details.Select(d => $"{d.Label}: {d.Value}")]);
        CopyCommand = new RelayCommand(() => _ = CopyAsync());
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        OpenWebsiteCommand = new RelayCommand(() => _open(new Uri(WebsiteUrl)));
        OpenRepositoryCommand = new RelayCommand(() => _open(new Uri(RepositoryUrl)));
        OpenLicenseCommand = new RelayCommand(() => _open(new Uri(LicenseUrl)));
        OpenNoticesCommand = new RelayCommand(() => _open(new Uri(NoticesUrl)));
    }

    /// <summary>Asks the window to close.</summary>
    public event EventHandler? CloseRequested;

    public string Title => Localizer.Get("command.help.about", "About Codalus IDE");

    public static string ProductName => ShellInfo.ProductName;

    /// <summary>"Version 0.12.0".</summary>
    public string Version { get; }

    public IReadOnlyList<AboutDetail> Details { get; }

    /// <summary>What Copy Info copies: the product and its version, then a line per detail.</summary>
    public string InfoText { get; }

    public ICommand CopyCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand OpenWebsiteCommand { get; }

    public ICommand OpenRepositoryCommand { get; }

    public ICommand OpenLicenseCommand { get; }

    public ICommand OpenNoticesCommand { get; }

    /// <summary>Copies <see cref="InfoText"/> (tests await it).</summary>
    public Task CopyAsync() => _copy(InfoText);
}
