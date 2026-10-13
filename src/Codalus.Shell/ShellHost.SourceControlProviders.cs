using Avalonia.Controls;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// Plugins' source control providers (plugin API 1.0, M22a): the Source Control Providers window shows each one's
/// groups of files and its commit box, rendered as a declarative view; opening a file compares it with the provider's
/// original. Git stays built in, in Git Changes.
/// </summary>
public sealed partial class ShellHost
{
    public const string SourceControlProvidersWindow = "scm.providers";

    private readonly Dictionary<string, string> _scmMessages = new(StringComparer.Ordinal);

    /// <summary>The window's content (one per window).</summary>
    public sealed class SourceControlProvidersContent
    {
        internal SourceControlProvidersContent(ShellHost shell) => Shell = shell;

        internal ShellHost Shell { get; }
    }

    private SourceControlProvidersContent? _scmContent;

    private void InitializeSourceControlProviders() =>
        Workbench.ToolWindows.Register(new ToolWindowDescriptor(SourceControlProvidersWindow, "Source Control Providers", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "git" });

    private SourceControlProvidersContent SourceControlProviders() => _scmContent ??= new SourceControlProvidersContent(this);

    /// <summary>The providers as a declarative view: each one's status, commit box and groups.</summary>
    internal ViewNodeDto SourceControlView()
    {
        var providers = Plugins.SourceControls;
        if (providers.Count == 0)
        {
            return new ViewNodeDto("stack") { Children = [new ViewNodeDto("text") { Text = "No plugin provides source control here. Git is in Git Changes.", Style = "muted" }] };
        }

        var children = new List<ViewNodeDto>();
        foreach (var provider in providers)
        {
            children.Add(new ViewNodeDto("text") { Text = provider.Label, Style = "heading" });
            children.Add(new ViewNodeDto("text") { Text = provider.Status is { Length: > 0 } status ? $"{provider.Root} · {status}" : provider.Root, Style = "muted" });
            children.Add(new ViewNodeDto("stack")
            {
                Orientation = "horizontal",
                Children =
                [
                    new ViewNodeDto("textbox") { Id = "message:" + provider.Id, Value = _scmMessages.GetValueOrDefault(provider.Id), Placeholder = provider.InputPlaceholder ?? "Message" },
                    new ViewNodeDto("button") { Id = "accept:" + provider.Id, Text = provider.AcceptLabel ?? "Commit", Style = "primary" },
                ],
            });
            foreach (var group in provider.Groups)
            {
                children.Add(new ViewNodeDto("text") { Text = $"{group.Label} ({group.Resources.Length})" });
                children.Add(new ViewNodeDto("table")
                {
                    Id = $"group:{provider.Id}:{group.Id}",
                    Columns = ["", "File"],
                    Rows = [.. group.Resources.Select(r => new[] { r.Status, Path.GetRelativePath(provider.Root, r.Path) })],
                });
            }

            children.Add(new ViewNodeDto("separator"));
        }

        return new ViewNodeDto("stack") { Children = [.. children] };
    }

    /// <summary>What the user did in the window: the commit box, the commit button, opening a file.</summary>
    internal async Task OnSourceControlEventAsync(string id, string kind, string? value, int? row)
    {
        var parts = id.Split(':');
        switch (parts[0], kind)
        {
            case ("message", "change"):
                _scmMessages[parts[1]] = value ?? string.Empty;
                break;
            case ("accept", "click"):
                var message = _scmMessages.GetValueOrDefault(parts[1]) ?? string.Empty;
                if (message.Trim().Length == 0)
                {
                    StatusBar.ShowMessage("Enter a message first.", TimeSpan.FromSeconds(4));
                    return;
                }

                try
                {
                    await Plugins.AcceptSourceControlAsync(parts[1], message, CancellationToken.None).ConfigureAwait(true);
                    _scmMessages.Remove(parts[1]);
                    _scmViewChanged?.Invoke();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Codalus.Rpc.JsonRpcRemoteException or Codalus.Rpc.JsonRpcConnectionLostException)
                {
                    Workbench.Notifications.Error($"The commit failed: {ex.Message}");
                }

                break;
            case ("group", "activate") when row is { } index && parts.Length >= 3:
                var provider = Plugins.SourceControls.FirstOrDefault(p => p.Id == parts[1]);
                if (provider?.Groups.FirstOrDefault(g => g.Id == string.Join(':', parts[2..]))?.Resources is { } resources && index < resources.Length)
                {
                    await OpenSourceControlDiffAsync(provider, resources[index].Path).ConfigureAwait(true);
                }

                break;
        }
    }

    private Action? _scmViewChanged;

    /// <summary>Compares the file with the provider's original (an empty one for a new file).</summary>
    public async Task OpenSourceControlDiffAsync(Workbench.Plugins.SourceControlInfo provider, string path)
    {
        var name = Path.GetFileName(path);
        var viewModel = new DiffViewModel(path, $"scm:{provider.Id}:{path}", $"{name} ({provider.Label})",
            async () => await Plugins.GetSourceControlOriginalAsync(provider.Id, path, CancellationToken.None).ConfigureAwait(false) ?? string.Empty,
            name, async () => File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : string.Empty);
        var document = new DiffDocument(viewModel, EditorFont);
        await document.ViewModel.LoadAsync().ConfigureAwait(true);
        Layout.OpenDocument(document, focus: true);
    }

    /// <summary>The window: a declarative view rebuilt when a provider changes.</summary>
    internal sealed class SourceControlProvidersView : UserControl
    {
        private readonly DeclarativeView _view = new();

        public SourceControlProvidersView(ShellHost shell)
        {
            Content = _view;
            void Refresh() => Ui.Post(() => _view.Root = shell.SourceControlView());
            shell.Plugins.SourceControlsChanged += (_, _) => Refresh();
            shell._scmViewChanged = Refresh;
            _view.Raised += (id, kind, value, row) => _ = shell.OnSourceControlEventAsync(id, kind, value, row);
            _view.Root = shell.SourceControlView();
        }

        /// <summary>The renderer (tests).</summary>
        public DeclarativeView View => _view;
    }
}
