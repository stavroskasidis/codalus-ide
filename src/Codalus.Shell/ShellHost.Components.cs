using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Controls;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Components;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Node;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// The Components window (M22d): what Codalus IDE downloads on demand, with version, size and licence, to install
/// ahead, remove, or carry to an air-gapped machine as an offline layout. A declarative view.
/// </summary>
public sealed partial class ShellHost
{
    public const string ComponentsWindow = "components";

    private ComponentCatalog? _components;
    private IReadOnlyList<ComponentStatus> _componentStatus = [];
    private int _selectedComponent = -1;
    private string? _componentsMessage;
    private Action? _componentsChanged;

    public sealed class ComponentsContent
    {
        internal ComponentsContent(ShellHost shell) => Shell = shell;

        internal ShellHost Shell { get; }
    }

    private ComponentsContent? _componentsContent;

    private ComponentsContent ComponentsPage() => _componentsContent ??= new ComponentsContent(this);

    /// <summary>The catalog: the components under the node cache (<c>CODALUS_NODE_CACHE</c>, else the cache folder).</summary>
    internal ComponentCatalog Components => _components ??= CreateComponents();

    private ComponentCatalog CreateComponents()
    {
        var cache = Environment.GetEnvironmentVariable(NodeCacheVariable) is { Length: > 0 } shared ? shared : Paths.Cache;
        string Relative(string folder) => Path.GetRelativePath(cache, folder);
        var components = new List<Component>
        {
            new()
            {
                Id = "node", Name = "Node.js", Version = NodeRuntime.Version, License = "MIT",
                Description = "Runs the web language servers, ESLint, Prettier and the browser debugger.",
                Folders = () => [Relative(Node.Folder)], IsInstalled = () => Node.IsInstalled, Install = Node.EnsureAsync,
            },
            new()
            {
                Id = "js-debug", Name = "vscode-js-debug", Version = JsDebug.Version, License = "MIT",
                Description = "Debugs JavaScript and Blazor WebAssembly in the browser.",
                Folders = () => [Relative(JsDebug.Folder)], IsInstalled = () => JsDebug.IsInstalled, Install = JsDebug.EnsureAsync,
            },
            new()
            {
                Id = "fsharp", Name = "F# language service", Version = $"{Codalus.Workbench.FSharp.FSharpTools.FsAutoCompleteVersion}, {Codalus.Workbench.FSharp.FSharpTools.FantomasVersion}", License = "Apache-2.0",
                Description = "F# editing and formatting (FsAutoComplete and Fantomas, installed with dotnet tool install).",
                Folders = () => [Relative(FSharp.FsAutoCompleteFolder), Relative(FSharp.FantomasFolder)], IsInstalled = () => FSharp.IsInstalled, Install = FSharp.EnsureAsync,
            },
            new()
            {
                Id = "netcoredbg-remote", Name = "netcoredbg for remote targets", Version = Codalus.Workbench.Debugging.Remote.RemoteDebuggers.Version, License = "MIT",
                Description = "Copied to SSH machines, containers and WSL to debug there (one download per platform, on first use).",
                Folders = () => [Path.Combine("netcoredbg", Codalus.Workbench.Debugging.Remote.RemoteDebuggers.Version)],
                IsInstalled = () => Directory.Exists(Path.Combine(cache, "netcoredbg", Codalus.Workbench.Debugging.Remote.RemoteDebuggers.Version)),
            },
            new()
            {
                Id = "icon-themes", Name = "Converted icon themes", License = "Each theme's own",
                Description = "Plugins' SVG file icons, converted once into the IDE's vector drawings.",
                Folders = () => [Relative(Path.Combine(Paths.Cache, "icon-themes"))],
                IsInstalled = () => Directory.Exists(Path.Combine(Paths.Cache, "icon-themes")),
            },
        };
        if (WebServers.IsAvailable)
        {
            components.Insert(1, new Component
            {
                Id = "web-language-servers", Name = "Web language servers", Version = Path.GetFileName(WebServers.Folder), License = "MIT and Apache-2.0",
                Description = "HTML, CSS, JavaScript and TypeScript editing, and ESLint (vscode-langservers-extracted, typescript-language-server).",
                Folders = () => [Relative(WebServers.Folder)], IsInstalled = () => WebServers.IsInstalled, Install = WebServers.EnsureAsync,
            });
        }

        return new ComponentCatalog(cache, components);
    }

    private void InitializeComponentsPage()
    {
        Workbench.ToolWindows.Register(new ToolWindowDescriptor(ComponentsWindow, "Components", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "package" });
        Workbench.Commands.Register(
            new CommandDescriptor("components.show", "Components…", ContributionSource.Shell) { Category = "Tools" },
            (_, _) =>
            {
                Ui.Post(() => Layout.ShowToolWindow(ComponentsWindow, focus: true));
                return Task.FromResult<JsonElement?>(null);
            });
        Workbench.Menus.Add([new MenuItemRule(MenuLocations.MenuBar("tools"), "components.show", ContributionSource.Shell) { Group = "plugins", Order = 101 }]);
    }

    /// <summary>Reads each component's state (folder sizes: off the UI thread), then redraws.</summary>
    internal async Task RefreshComponentsAsync()
    {
        var catalog = Components;
        _componentStatus = await Task.Run(catalog.Status).ConfigureAwait(true);
        _componentsChanged?.Invoke();
    }

    internal ViewNodeDto ComponentsRoot()
    {
        static string Size(long bytes) => bytes == 0 ? "" : bytes >= 1 << 20 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
        var children = new List<ViewNodeDto>
        {
            new("text") { Text = "Downloaded when a feature first needs them, into " + Components.CacheFolder + ".", Style = "muted" },
            new("table")
            {
                Id = "components",
                Columns = ["Component", "Version", "Status", "Size", "Licence"],
                Rows = [.. _componentStatus.Select(s => new[]
                {
                    s.Component.Name, s.Component.Version ?? string.Empty, s.Installed ? "installed" : "not downloaded", Size(s.Size), s.Component.License,
                })],
            },
        };
        if (_selectedComponent >= 0 && _selectedComponent < _componentStatus.Count)
        {
            var selected = _componentStatus[_selectedComponent];
            children.Add(new ViewNodeDto("text") { Text = selected.Component.Description });
            children.Add(new ViewNodeDto("toolbar")
            {
                Children =
                [
                    new ViewNodeDto("button") { Id = "install", Text = "Download Now", Disabled = selected.Installed || selected.Component.Install is null },
                    new ViewNodeDto("button") { Id = "remove", Text = "Remove", Disabled = !selected.Installed },
                ],
            });
        }

        children.Add(new ViewNodeDto("separator"));
        children.Add(new ViewNodeDto("text") { Text = "Offline layout", Style = "heading" });
        children.Add(new ViewNodeDto("text") { Text = "Export the downloaded components to a folder, and import it on a machine without internet access: features find them there and download nothing.", Style = "muted" });
        children.Add(new ViewNodeDto("toolbar")
        {
            Children =
            [
                new ViewNodeDto("button") { Id = "export", Text = "Export Offline Layout…" },
                new ViewNodeDto("button") { Id = "import", Text = "Import Offline Layout…" },
                new ViewNodeDto("button") { Id = "refresh", Text = "Refresh" },
            ],
        });
        if (_componentsMessage is { } message)
        {
            children.Add(new ViewNodeDto("text") { Text = message, Style = "muted" });
        }

        return new ViewNodeDto("stack") { Children = [.. children] };
    }

    /// <summary>What the user did in the window. <paramref name="folder"/> stands in for the folder picker (tests).</summary>
    internal async Task OnComponentsEventAsync(string id, string kind, int? row, string? folder = null)
    {
        switch (id, kind)
        {
            case ("components", "select" or "activate"):
                _selectedComponent = row ?? -1;
                break;
            case ("install", "click") when _selectedComponent >= 0 && _selectedComponent < _componentStatus.Count && _componentStatus[_selectedComponent].Component.Install is { } install:
                var name = _componentStatus[_selectedComponent].Component.Name;
                var task = Workbench.Progress.Begin($"Downloading {name}", "components");
                try
                {
                    await Task.Run(() => install(new Progress<string>(m => task.Report(m, null)), CancellationToken.None)).ConfigureAwait(true);
                    _componentsMessage = $"{name} is installed.";
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException)
                {
                    _componentsMessage = $"{name} couldn't be downloaded: {ex.Message}";
                }
                finally
                {
                    task.End();
                }

                await RefreshComponentsAsync().ConfigureAwait(true);
                return;
            case ("remove", "click") when _selectedComponent >= 0 && _selectedComponent < _componentStatus.Count:
                var component = _componentStatus[_selectedComponent].Component;
                if (!await Dialogs.ConfirmAsync($"Remove {component.Name}?", "It is downloaded again when a feature needs it.", "Remove", danger: true).ConfigureAwait(true))
                {
                    return;
                }

                try
                {
                    Components.Remove(component);
                    _componentsMessage = $"{component.Name} is removed.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _componentsMessage = $"{component.Name} is in use: {ex.Message}";
                }

                await RefreshComponentsAsync().ConfigureAwait(true);
                return;
            case ("export", "click"):
                if ((folder ?? (Window is { } exportWindow ? await exportWindow.PickFolderAsync("Export an Offline Layout").ConfigureAwait(true) : null)) is { } target)
                {
                    var exported = await Task.Run(() => Components.Export(target)).ConfigureAwait(true);
                    _componentsMessage = exported.Count == 0 ? "Nothing is downloaded yet, so the layout is empty." : $"Exported {string.Join(", ", exported.Select(e => e.Id))} to {target}.";
                }

                break;
            case ("import", "click"):
                if ((folder ?? (Window is { } importWindow ? await importWindow.PickFolderAsync("Import an Offline Layout").ConfigureAwait(true) : null)) is { } layout)
                {
                    try
                    {
                        var imported = await Task.Run(() => Components.Import(layout)).ConfigureAwait(true);
                        _componentsMessage = imported.Count == 0 ? "The layout has nothing this version needs that isn't here already." : $"Imported {string.Join(", ", imported)}.";
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
                    {
                        _componentsMessage = ex.Message;
                    }

                    await RefreshComponentsAsync().ConfigureAwait(true);
                    return;
                }

                break;
            case ("refresh", "click"):
                await RefreshComponentsAsync().ConfigureAwait(true);
                return;
            default:
                return;
        }

        _componentsChanged?.Invoke();
    }

    internal sealed class ComponentsView : UserControl
    {
        private readonly DeclarativeView _view = new();

        public ComponentsView(ShellHost shell)
        {
            Content = new ScrollViewer { Content = _view };
            shell._componentsChanged = () => _view.Root = shell.ComponentsRoot();
            _view.Raised += (id, kind, value, row) => _ = shell.OnComponentsEventAsync(id, kind, row);
            _view.Root = shell.ComponentsRoot();
            _ = shell.RefreshComponentsAsync();
        }

        public DeclarativeView View => _view;
    }
}
