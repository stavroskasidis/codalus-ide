using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Views;

/// <summary>The standard toolbar (M29): see <see cref="ViewModels.ToolbarViewModel"/>.</summary>
public sealed partial class StandardToolbar : UserControl
{
    private readonly List<Control> _builtIn;
    private readonly MenuFlyout _launchMenu = new() { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedLeft };
    private ToolbarViewModel? _model;

    public StandardToolbar()
    {
        InitializeComponent();
        HotReload.Content = CommandIcons.Create("run.startWithHotReload");
        StartButton.Flyout = _launchMenu;
        _launchMenu.Opening += (_, _) => _model?.LaunchMenuOpening?.Invoke();
        _builtIn = [.. Panel.Children];
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.CustomizationChanged -= OnCustomizationChanged;
                _model.LaunchMenuChanged -= OnLaunchMenuChanged;
                _model.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = DataContext as ToolbarViewModel;
            if (_model is not null)
            {
                _model.CustomizationChanged += OnCustomizationChanged;
                _model.LaunchMenuChanged += OnLaunchMenuChanged;
                _model.PropertyChanged += OnModelPropertyChanged;
            }

            Rebuild();
            FillLaunchMenu();
        };
    }

    /// <summary>The Start drop-down's items (tests).</summary>
    internal MenuFlyout LaunchMenu => _launchMenu;

    private void OnLaunchMenuChanged(object? sender, EventArgs e) => FillLaunchMenu();

    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolbarViewModel.StartLabel))
        {
            FillLaunchMenu();
        }
    }

    /// <summary>
    /// The Start drop-down, filled whenever its choices change (not as it opens), so the narrow window's "…" copies it
    /// as it is: Start (and, for a set of startup projects, Configure Startup Projects only); the launch profiles, the chosen one checked and the ones Codalus can't run disabled with the
    /// reason; for a web app the debugging browser and script debugging; the project's Debug properties.
    /// </summary>
    private void FillLaunchMenu()
    {
        var items = _launchMenu.Items;
        items.Clear();
        if (_model is not { } model)
        {
            return;
        }

        items.Add(MenuEntry.Create(model.StartLabel, "play", model.StartCommand));
        if (model.IsStartupSet)
        {
            // A set of startup projects (ADR 0110): each project's profile is the set's, chosen on the Startup Projects page.
            items.Add(new Separator());
            items.Add(MenuEntry.Create(Localizer.Get("standardToolbar.configureStartupProjects", "Configure Startup Projects…"), "tools", model.ConfigureStartupProjectsCommand));
            return;
        }

        if (model.LaunchMenu is not { } menu)
        {
            return;
        }

        if (menu.Profiles.Count > 0)
        {
            items.Add(new Separator());
            foreach (var profile in menu.Profiles)
            {
                var item = MenuEntry.Create(profile.Name, null, model.SelectLaunchProfileCommand, profile.Name);
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = profile.Name == menu.Current;
                if (profile.Unsupported is { } reason)
                {
                    item.IsEnabled = false;
                    ToolTip.SetTip(item, reason);
                    ToolTip.SetShowOnDisabled(item, true);
                    AutomationProperties.SetHelpText(item, reason);
                }

                items.Add(item);
            }
        }

        if (menu.Web is { } web)
        {
            items.Add(new Separator());
            var browser = MenuEntry.Create(Localizer.Format("standardToolbar.webBrowser", "Web Browser ({0})", web.BrowserName ?? Localizer.Get("standardToolbar.noBrowser", "none found")), "globe");
            foreach (var choice in web.Browsers)
            {
                var item = MenuEntry.Create(choice.Label, null, model.SelectBrowserCommand, choice.Setting);
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = choice.Setting == web.Browser;
                item.IsEnabled = choice.Installed;
                browser.Items.Add(item);
            }

            items.Add(browser);
            var enabled = Localizer.Get("standardToolbar.scriptDebuggingEnabled", "Enabled");
            var disabled = Localizer.Get("standardToolbar.scriptDebuggingDisabled", "Disabled");
            var script = MenuEntry.Create(Localizer.Format("standardToolbar.scriptDebugging", "Script Debugging ({0})", web.ScriptDebugging ? enabled : disabled), null);
            foreach (var (label, value) in new[] { (enabled, true), (disabled, false) })
            {
                var item = MenuEntry.Create(label, null, model.SelectScriptDebuggingCommand, value);
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = web.ScriptDebugging == value;
                script.Items.Add(item);
            }

            items.Add(script);
        }

        items.Add(new Separator());
        items.Add(MenuEntry.Create(Localizer.Format("standardToolbar.debugProperties", "{0} Debug Properties", menu.ProjectName), "tools", model.DebugPropertiesCommand));
    }

    private void OnCustomizationChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>
    /// The built-in buttons but the ones the user removed, then the commands they added and their own toolbars (M23b,
    /// Tools → Customize), each group after a separator.
    /// </summary>
    private void Rebuild()
    {
        var removed = _model?.Removed ?? new HashSet<string>();
        Panel.Children.Clear();
        foreach (var control in _builtIn)
        {
            if (control is not (Button or SplitButton) || control.Tag is not string command || !removed.Contains(command))
            {
                Panel.Children.Add(control);
            }
        }

        foreach (var (toolbar, items) in _model?.Extra ?? [])
        {
            if (items.Count == 0)
            {
                continue;
            }

            var separator = new Border { Classes = { "toolbarSeparator" } };
            if (toolbar is not null)
            {
                ToolTip.SetTip(separator, toolbar);
            }

            Panel.Children.Add(separator);
            foreach (var item in items)
            {
                var button = new Button
                {
                    Classes = { "toolbar" },
                    Command = item.Run,
                    Tag = item.Command,
                    Content = CommandIcons.Create(item.Command) ?? (object)new TextBlock { Text = item.Title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                };
                AutomationProperties.SetName(button, item.Title);
                ToolTip.SetTip(button, item.Title);
                Panel.Children.Add(button);
            }
        }

        UpdateTips();
    }

    /// <summary>
    /// Tooltips name the button and its shortcut on this OS (M34a, D25): each button's <c>Tag</c> is its command, whose
    /// key binding the view model reports (Undo and Redo use the editor's own keys).
    /// </summary>
    private void UpdateTips()
    {
        if (DataContext is not ToolbarViewModel { KeyLabel: { } keyLabel })
        {
            return;
        }

        foreach (var button in this.GetLogicalDescendants().OfType<ContentControl>().Where(c => c is Button or SplitButton))
        {
            if (button.Tag is string command && AutomationProperties.GetName(button) is { Length: > 0 } name)
            {
                ToolTip.SetTip(button, keyLabel(command) is { } keys ? $"{name} ({keys})" : name);
            }
        }
    }
}
