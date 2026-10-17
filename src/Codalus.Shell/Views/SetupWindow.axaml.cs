using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The first-run setup's window (M46). Keys: Tab moves through the step; the arrow keys move between the cards and modes
/// of a group (and choose); Space or Enter presses the focused one; Alt+Right is Next (Done on the last step), Alt+Left
/// Back; Escape skips the setup. File and folder pickers open over this window.
/// </summary>
public sealed partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        Opened += (_, _) => FocusStep();

        // Before the scroll viewers, which take the arrow keys to scroll.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (Model is { } model)
            {
                model.PickFile = PickFileAsync;
                model.PickFolder = PickFolderAsync;
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SetupViewModel.Current))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(FocusStep, Avalonia.Threading.DispatcherPriority.Loaded);
                    }
                };
            }
        };
    }

    private SetupViewModel? Model => DataContext as SetupViewModel;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                model.SkipCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.Alt:
                model.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt:
                model.BackCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left or Key.Up or Key.Right or Key.Down when e.KeyModifiers == KeyModifiers.None:
                e.Handled = MoveWithinGroup(e.Key is Key.Left or Key.Up ? -1 : 1);
                break;
        }
    }

    /// <summary>The arrow keys within the focused group of cards or modes: the next one is chosen and focused.</summary>
    private bool MoveWithinGroup(int delta)
    {
        if (FocusManager?.GetFocusedElement() is not ToggleButton focused)
        {
            return false;
        }

        foreach (var list in new[] { FamilyList, ModeList, SchemeList })
        {
            var buttons = list.GetVisualDescendants().OfType<ToggleButton>().ToList();
            var index = buttons.IndexOf(focused);
            if (index < 0)
            {
                continue;
            }

            var next = buttons[Math.Clamp(index + delta, 0, buttons.Count - 1)];
            next.Command?.Execute(null);
            next.Focus(NavigationMethod.Directional);
            return true;
        }

        return false;
    }

    /// <summary>A step's first control: the chosen family, the chosen scheme, the first import, the Next button.</summary>
    private void FocusStep()
    {
        Control? target = Model?.Current switch
        {
            SetupStep.Appearance => Chosen(FamilyList),
            SetupStep.Keyboard => Chosen(SchemeList),
            SetupStep.Settings => ImportVsCodeButton.IsEnabled ? ImportVsCodeButton : null,
            _ => null,
        };
        (target ?? NextButton).Focus();
    }

    private static ToggleButton? Chosen(ItemsControl list)
    {
        var buttons = list.GetVisualDescendants().OfType<ToggleButton>().ToList();
        return buttons.FirstOrDefault(b => b.IsChecked == true) ?? buttons.FirstOrDefault();
    }

    private async Task<string?> PickFileAsync(string title, string filterName, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        }).ConfigureAwait(true);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }).ConfigureAwait(true);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
