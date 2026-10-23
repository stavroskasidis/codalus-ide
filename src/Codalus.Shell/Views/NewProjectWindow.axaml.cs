using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// New Project's window (0.6.0, ADR 0082): the three pages of <see cref="NewProjectViewModel"/>. Keys, as the classic
/// IDE's wizard: Alt+Left is Back, Alt+Right (and Enter) Next or Create (on macOS ⌘[ and ⌘] too, and ⌥← and ⌥→ stay
/// the text boxes' word moves while one has the focus); Escape closes (or clears the search first, and
/// leaves an open drop-down to close itself); Alt+S goes to the search and Down from it into the templates; Enter or a
/// double-click on a template goes on; Delete removes a recent template. Each page focuses its first field when it
/// opens (the project name with its text selected); Tab follows the reading order; the labels' access keys focus their
/// fields.
/// </summary>
public sealed partial class NewProjectWindow : Window
{
    private NewProjectViewModel? _model;
    private bool _opened;

    public NewProjectWindow()
    {
        InitializeComponent();

        // Before the lists and text boxes, which take Enter, Escape and the arrows for themselves.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            _opened = true;
            FocusPage();
        };
        TemplateList.SelectionChanged += (_, _) =>
        {
            if (TemplateList.SelectedItem is NewProjectTemplateRow row && _model is { } model && !ReferenceEquals(model.SelectedTemplate, row))
            {
                model.SelectedTemplate = row;
            }
        };
        RecentList.SelectionChanged += (_, _) =>
        {
            if (RecentList.SelectedItem is NewProjectTemplateRow row && _model is { } model && !ReferenceEquals(model.SelectedTemplate, row))
            {
                model.SelectedTemplate = row;
            }
        };
        TemplateList.DoubleTapped += OnRowDoubleTapped;
        RecentList.DoubleTapped += OnRowDoubleTapped;
        DataContextChanged += (_, _) => Attach(DataContext as NewProjectViewModel);
    }

    /// <summary>The location's path box (tests set its picker).</summary>
    internal PathBox LocationField => LocationBox;

    private void Attach(NewProjectViewModel? model)
    {
        if (_model is not null)
        {
            _model.PageEntered -= OnPageEntered;
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (model is not null)
        {
            model.PageEntered += OnPageEntered;
            model.PropertyChanged += OnModelChanged;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The recent list shows its selection only while a recent template is the one chosen.
        if (e.PropertyName == nameof(NewProjectViewModel.SelectedTemplate) && _model is { } model && !ReferenceEquals(RecentList.SelectedItem, model.SelectedTemplate))
        {
            RecentList.SelectedItem = null;
        }
    }

    private void OnPageEntered(object? sender, NewProjectPage page)
    {
        if (_opened)
        {
            Dispatcher.UIThread.Post(FocusPage, DispatcherPriority.Loaded);
        }
    }

    /// <summary>The page's first field: the search (or the chosen template, coming back), the project name, the framework.</summary>
    private void FocusPage()
    {
        if (_model is not { } model)
        {
            return;
        }

        switch (model.Page)
        {
            case NewProjectPage.Choose when model.SelectedTemplate is { } row && TemplateList.ContainerFromItem(row) is Control container:
                container.Focus(NavigationMethod.Directional);
                break;
            case NewProjectPage.Choose:
                SearchBox.Focus();
                break;
            case NewProjectPage.Configure:
                var name = model.ShowProjectName ? ProjectNameBox : SolutionNameBox;
                name.Focus();
                name.SelectAll();
                break;
            case NewProjectPage.Additional:
                var first = FrameworkCombo.IsEffectivelyVisible ? FrameworkCombo
                    : AdditionalFields.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c is CheckBox or ComboBox or TextBox && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
                (first ?? CreateButton).Focus();
                break;
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null && source.FindAncestorOfType<Button>() is null && _model is { } model)
        {
            _ = model.GoForwardAsync();
            e.Handled = true;
        }
    }

    /// <summary>Whether a drop-down or a menu is open: Enter and Escape are theirs then.</summary>
    private bool IsPopupOpen() =>
        LocationBox.RecentMenu.IsOpen
        || this.GetVisualDescendants().OfType<ComboBox>().Any(c => c.IsDropDownOpen)
        || FocusManager?.GetFocusedElement() is MenuItem;

    /// <summary>Next (or Create) once the chosen template has been read: a template read from its help takes a moment.</summary>
    private static async Task GoOnAsync(NewProjectViewModel model)
    {
        await model.WaitForTemplateAsync().ConfigureAwait(true);
        await model.GoForwardAsync().ConfigureAwait(true);
    }

    /// <summary>On macOS ⌥← and ⌥→ move the caret by word in a text box: the wizard leaves them to it (⌘[ and ⌘] go back and on).</summary>
    private static bool IsWordMove(NewProjectViewModel model, IInputElement? focused) => model.IsMacOS && focused is TextBox;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        var focused = FocusManager?.GetFocusedElement();
        switch (e.Key)
        {
            case Key.Escape when !IsPopupOpen():
                if (ReferenceEquals(focused, SearchBox) && model.HasSearch)
                {
                    model.Search = string.Empty;
                }
                else
                {
                    model.Cancel();
                }

                e.Handled = true;
                break;
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt && !IsWordMove(model, focused):
            case Key.OemOpenBrackets when e.KeyModifiers == KeyModifiers.Meta && model.IsMacOS:
                model.GoBack();
                e.Handled = true;
                break;
            case Key.Right when e.KeyModifiers == KeyModifiers.Alt && !IsWordMove(model, focused):
            case Key.OemCloseBrackets when e.KeyModifiers == KeyModifiers.Meta && model.IsMacOS:
                _ = model.GoForwardAsync();
                e.Handled = true;
                break;
            case Key.S when e.KeyModifiers == KeyModifiers.Alt && model.IsChoosePage:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None && ReferenceEquals(focused, SearchBox) && model.Templates.Count > 0:
                var row = model.SelectedTemplate is { } selected && model.Templates.Contains(selected) ? selected : model.Templates[0];
                model.SelectedTemplate = row;
                TemplateList.ScrollIntoView(row);
                Dispatcher.UIThread.Post(() => (TemplateList.ContainerFromItem(row) as Control)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
                e.Handled = true;
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.None && RecentList.IsKeyboardFocusWithin && RecentList.SelectedItem is NewProjectTemplateRow recent:
                recent.RemoveCommand.Execute(null);
                e.Handled = true;
                break;
            // A focused button presses itself; a check box or a combo box leaves Enter to the wizard.
            case Key.Enter when e.KeyModifiers == KeyModifiers.None && focused is not Button or Avalonia.Controls.Primitives.ToggleButton && !IsPopupOpen():
                // From the search box, Enter takes the best match of what's typed, unless a row it shows is chosen.
                if (model.IsChoosePage && ReferenceEquals(focused, SearchBox))
                {
                    model.ApplyPendingSearch();
                }

                if (model.IsChoosePage && model.Templates.Count > 0
                    && (model.SelectedTemplate is not { } chosen || !(model.Templates.Contains(chosen) || model.RecentTemplates.Contains(chosen))))
                {
                    model.SelectedTemplate = model.Templates[0];
                }

                _ = GoOnAsync(model);
                e.Handled = true;
                break;
        }
    }
}
