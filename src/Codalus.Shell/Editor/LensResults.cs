using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Location = Codalus.LanguageServices.Protocol.Location;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor;

/// <summary>The values of <c>editor.codeLensStyle</c> (Options → Environment → Appearance → CodeLens Style); popup by default.</summary>
public static class CodeLensStyles
{
    /// <summary>The popup style, the default: <c>3 usages | 2 inheritors</c>; a click lists them in a popup under the row.</summary>
    public const string Popup = "popup";

    /// <summary>The classic IDE's: <c>3 references | 2 implementations</c>; a click opens the References window.</summary>
    public const string Window = "window";
}

/// <summary>What a CodeLens count counts, which decides its wording and its popup's title.</summary>
public enum LensSubject
{
    /// <summary>Another language's lens, or one this editor doesn't know: its title and command stay as they are.</summary>
    Other,

    /// <summary>The references to a type or member.</summary>
    Usages,

    /// <summary>The classes derived from a class (its inheritors).</summary>
    DerivedTypes,

    /// <summary>The types that implement an interface.</summary>
    Implementations,

    /// <summary>The members that implement or override an interface's, abstract or virtual member.</summary>
    MemberImplementations,
}

/// <summary>
/// A click on a CodeLens title: its command (the language service's), what it counts, the lens's symbol
/// (<paramref name="Position"/>, in the pane that was clicked, which may be the split one), where its popup goes (the
/// bottom of the title in <paramref name="Target"/>'s coordinates), and what takes the focus back when it closes.
/// </summary>
public sealed record CodeLensClick(string CommandId, LensSubject Subject, Position Position, Visual Target, Point Anchor, InputElement ReturnFocus);

/// <summary>The wording of the C# service's CodeLens titles in each style.</summary>
public static partial class LensTitles
{
    /// <summary>The kind the C# host gives a lens in its data (<c>references</c> or <c>implementations</c>), or null for another server's.</summary>
    public static string? KindOf(CodeLens lens) =>
        lens.Data is { ValueKind: JsonValueKind.Array } data && data.GetArrayLength() == 4 && data[3].ValueKind == JsonValueKind.String
            ? data[3].GetString()
            : null;

    /// <summary>
    /// What a lens of <paramref name="kind"/> counts, from the declaration's text before its name
    /// (<c>public abstract class </c>).
    /// </summary>
    public static LensSubject Subject(string? kind, string declaration) => kind switch
    {
        "references" => LensSubject.Usages,
        "implementations" => TypeKeyword().Match(declaration) is { Success: true } keyword
            ? keyword.Value == "interface" ? LensSubject.Implementations : LensSubject.DerivedTypes
            : LensSubject.MemberImplementations,
        _ => LensSubject.Other,
    };

    /// <summary>
    /// A title as the row shows it: in the popup style, <c>usages</c> for references (<c>3 usages</c>) and
    /// <c>inheritors</c> for a class's derived types (<c>2 inheritors</c>); the service's own (<c>3 references</c>) in the
    /// References window style.
    /// </summary>
    public static string Display(string title, string? kind, string declaration, bool popups)
    {
        if (!popups || CountTitle().Match(title) is not { Success: true } match
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
        {
            return title;
        }

        return Subject(kind, declaration) switch
        {
            LensSubject.Usages => Count(count, "usage"),
            LensSubject.DerivedTypes => Count(count, "inheritor"),
            _ => title,
        };
    }

    private static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? noun : noun + "s");

    [GeneratedRegex(@"\b(class|record|interface)\b")]
    private static partial Regex TypeKeyword();

    [GeneratedRegex(@"^(\d+) (references?|implementations?)$")]
    private static partial Regex CountTitle();
}

/// <summary>One row of a CodeLens popup: a symbol or a use, its icon, what follows it muted, and its project.</summary>
public sealed record LensResultItem(string Icon, string Name, string? Detail, string? Project, Location Location);

/// <summary>
/// The popup style's CodeLens list: under the clicked count, its title (<c>Derived types of 'Shape'</c>)
/// with a button that shows the results in the References window instead, then a row per result. Up and Down move,
/// Enter or a click opens the row's location, Escape (or a click elsewhere) closes.
/// </summary>
public sealed class LensResultsPresenter
{
    private readonly Popup _popup;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListBox _list = new() { MaxHeight = 320, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(4) };
    private readonly Control _owner;
    private IReadOnlyList<LensResultItem> _items = [];
    private Func<Location, Task>? _open;
    private Action? _openWindow;

    public LensResultsPresenter(Control owner)
    {
        _owner = owner;
        var windowButton = new Button { Classes = { "toolbar" }, Content = new Icon { Kind = "references", Width = 14, Height = 14 }, Margin = new Thickness(4, 2) };
        var windowName = Localizer.Get("codeLens.popup.openInWindow", "Show in the References window");
        AutomationProperties.SetName(windowButton, windowName);
        ToolTip.SetTip(windowButton, windowName);
        windowButton.Click += (_, _) => OpenWindow();
        var header = new DockPanel { Height = 30, Children = { windowButton, _title } };
        DockPanel.SetDock(windowButton, Avalonia.Controls.Dock.Right);
        var headerBorder = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) };
        headerBorder.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("Shell.Border"));
        var body = new DockPanel { Children = { headerBorder, _list } };
        DockPanel.SetDock(headerBorder, Avalonia.Controls.Dock.Top);
        var frame = new Border { Child = body, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), MinWidth = 360, MaxWidth = 720, ClipToBounds = true };
        frame.Bind(Border.BackgroundProperty, owner.GetResourceObservable("Shell.Panel"));
        frame.Bind(Border.BorderBrushProperty, owner.GetResourceObservable("Shell.Border"));
        _popup = new Popup
        {
            Child = frame,
            IsLightDismissEnabled = true,
            WindowManagerAddShadowHint = true,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.ResizeY,
        };
        // On the way down (tunnel), before the list takes Enter for its own: the popup's rows are in its own root.
        frame.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && e.Source is Visual source && (source == _list || _list.IsVisualAncestorOf(source)))
            {
                // A row's: the header's button (which can take the focus) takes its own Enter.
                Choose(_list.SelectedIndex);
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AutomationProperties.SetName(frame, Localizer.Get("codeLens.popup.name", "CodeLens results"));
    }

    /// <summary>The popup; the editor view adds it to its tree.</summary>
    public Popup Popup => _popup;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The popup's title (tests).</summary>
    public string Title => _title.Text ?? string.Empty;

    /// <summary>Its rows (tests).</summary>
    public IReadOnlyList<LensResultItem> Items => _items;

    /// <summary>Opens the popup at <paramref name="anchor"/> in <paramref name="target"/>, its first row selected and focused.</summary>
    public void Show(Visual target, Point anchor, string title, IReadOnlyList<LensResultItem> items, Func<Location, Task> open, Action openWindow)
    {
        _title.Text = title;
        _items = items;
        _open = open;
        _openWindow = openWindow;
        _list.Items.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            var index = i;
            var row = new ListBoxItem { Content = Row(items[i]), Padding = new Thickness(6, 4), CornerRadius = new CornerRadius(4) };
            AutomationProperties.SetName(row, items[i].Name + (items[i].Detail is { } detail ? " " + detail : string.Empty));
            row.Tapped += (_, e) =>
            {
                Choose(index);
                e.Handled = true;
            };
            _list.Items.Add(row);
        }

        _list.SelectedIndex = items.Count > 0 ? 0 : -1;
        _popup.PlacementTarget = target as Control ?? _owner;
        _popup.PlacementRect = new Rect(anchor.X, anchor.Y, 1, 1);
        _popup.IsOpen = true;
        FocusSelected();

        // The rows' containers are made in the popup's first layout: the keys go to the first row once it's there.
        Avalonia.Threading.Dispatcher.UIThread.Post(FocusSelected, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void FocusSelected()
    {
        if (!_popup.IsOpen)
        {
            return;
        }

        if (_list.ContainerFromIndex(Math.Max(0, _list.SelectedIndex)) is ListBoxItem row)
        {
            row.Focus(NavigationMethod.Directional);
        }
        else
        {
            _list.Focus(NavigationMethod.Directional);
        }
    }

    private Grid Row(LensResultItem item)
    {
        var name = new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        var detail = new TextBlock { Text = item.Detail ?? string.Empty, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        detail.Bind(TextBlock.ForegroundProperty, _owner.GetResourceObservable("Shell.MutedForeground"));
        var project = new TextBlock { Text = item.Project ?? string.Empty, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
        project.Bind(TextBlock.ForegroundProperty, _owner.GetResourceObservable("Shell.MutedForeground"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var icon = new Icon { Kind = item.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(detail, 2);
        Grid.SetColumn(project, 3);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(detail);
        grid.Children.Add(project);
        return grid;
    }

    /// <summary>Opens the location of the row at <paramref name="index"/> and closes the popup.</summary>
    public void Choose(int index)
    {
        if (index < 0 || index >= _items.Count || _open is not { } open)
        {
            return;
        }

        Close();
        _ = open(_items[index].Location);
    }

    /// <summary>Shows the results in the References window instead, and closes the popup.</summary>
    public void OpenWindow()
    {
        Close();
        _openWindow?.Invoke();
    }

    public void Close() => _popup.IsOpen = false;
}
