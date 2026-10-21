using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Controls;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>
/// Signature help as a classic IDE shows it: the overloads' chevrons with "1 of 2" between them (Up and Down, or a
/// click, move through them), the colorized signature with the active parameter bold, then the method's summary and the
/// active parameter's documentation, its name bold and italic. It sits above the caret's line when there's room.
/// </summary>
public sealed class SignatureHelpPopup : IntelliSensePopup
{
    private readonly Func<EditorTheme> _theme;
    private readonly StackPanel _overloads;
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) };
    private readonly ContentControl _signature = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _parameter = new() { TextWrapping = TextWrapping.Wrap };
    private SignatureHelp _help;
    private int _selectedIndex;

    public SignatureHelpPopup(TextArea textArea, SignatureHelp help, Func<EditorTheme> theme) : base(textArea)
    {
        _theme = theme;
        _help = help;
        _selectedIndex = Math.Clamp(help.ActiveSignature, 0, Math.Max(0, help.Signatures.Length - 1));
        AnchorOffset = textArea.Caret.Offset;

        var up = Chevron("chevron-up", Localizer.Get("editor.signatureHelp.previous", "Previous overload (Up)"), -1);
        var down = Chevron("chevron-down", Localizer.Get("editor.signatureHelp.next", "Next overload (Down)"), +1);
        _overloads = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 6, 0), Children = { up, _position, down } };
        _overloads.Classes.Add("signatureOverloads");
        var header = new DockPanel();
        DockPanel.SetDock(_overloads, Avalonia.Controls.Dock.Left);
        header.Children.Add(_overloads);
        header.Children.Add(_signature);

        Child = new Border
        {
            Classes = { "intelliSense", "signatureHelp" },
            MaxWidth = 720,
            Child = new StackPanel { Spacing = 4, Children = { header, _summary, _parameter } },
        };
        Update();
    }

    /// <summary>The offset whose position anchors it horizontally (where it opened), so it doesn't follow every keystroke.</summary>
    public int AnchorOffset { get; set; }

    public SignatureHelp Help => _help;

    /// <summary>The number of overloads.</summary>
    public int Count => _help.Signatures.Length;

    /// <summary>The overload shown.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = Math.Clamp(value, 0, Math.Max(0, Count - 1));
            Update();
        }
    }

    /// <summary>The overload shown.</summary>
    public SignatureInformation Current => _help.Signatures[_selectedIndex];

    /// <summary>The active parameter of the overload shown, or null.</summary>
    public int? ActiveParameter => Current.ActiveParameter ?? (_help.ActiveParameter < Current.Parameters.Length ? _help.ActiveParameter : null);

    /// <summary>"1 of 2" (tests).</summary>
    public string PositionText => _position.Text ?? string.Empty;

    /// <summary>The colorized signature (tests).</summary>
    public TextBlock SignatureBlock => (TextBlock)_signature.Content!;

    /// <summary>The summary and the active parameter's documentation (tests).</summary>
    public TextBlock SummaryBlock => _summary;

    public TextBlock ParameterBlock => _parameter;

    public override double PreferredHeight
    {
        get
        {
            if (Child is not { } child)
            {
                return 60;
            }

            child.Measure(new Size(720, double.PositiveInfinity));
            return Math.Max(24, child.DesiredSize.Height);
        }
    }

    /// <summary>New help for the same call (another argument, an edit): keeps the overload the user chose when it still applies.</summary>
    public void Update(SignatureHelp help, bool keepSelection)
    {
        var keep = _selectedIndex;
        _help = help;
        _selectedIndex = keepSelection && keep < help.Signatures.Length ? keep : Math.Clamp(help.ActiveSignature, 0, Math.Max(0, help.Signatures.Length - 1));
        Update();
    }

    /// <summary>Shows the previous or next overload, wrapping around; false when there is only one.</summary>
    public bool ChangeIndex(int delta)
    {
        if (Count <= 1)
        {
            return false;
        }

        SelectedIndex = ((_selectedIndex + delta) % Count + Count) % Count;
        return true;
    }

    private Button Chevron(string icon, string name, int delta)
    {
        var button = new Button
        {
            Classes = { "signatureChevron" },
            Focusable = false,
            Content = new Icon { Kind = icon, Width = 12, Height = 12 },
        };
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, e) =>
        {
            ChangeIndex(delta);
            e.Handled = true;
        };
        return button;
    }

    private void Update()
    {
        if (Count == 0)
        {
            return;
        }

        var theme = _theme();
        var signature = Current;
        var active = ActiveParameter;
        _overloads.IsVisible = Count > 1;
        _position.Text = Localizer.Format("editor.signatureHelp.position", "{0} of {1}", _selectedIndex + 1, Count);

        var block = ClassifiedInlines.Block(ClassifiedInlines.Of(signature), theme, active);
        block.Classes.Add("signature");
        _signature.Content = block;

        var summary = signature.Documentation?.Value is { Length: > 0 } doc ? ClassifiedInlines.PlainText(doc) : null;
        _summary.Text = summary;
        _summary.IsVisible = summary is { Length: > 0 };

        _parameter.Inlines!.Clear();
        if (active is { } index && index < signature.Parameters.Length && signature.Parameters[index].Documentation?.Value is { Length: > 0 } parameterDoc)
        {
            _parameter.Inlines.Add(new Run(ClassifiedInlines.ParameterName(signature, index) + ":") { FontWeight = FontWeight.Bold, FontStyle = FontStyle.Italic });
            _parameter.Inlines.Add(new Run(" " + ClassifiedInlines.PlainText(parameterDoc)));
            _parameter.IsVisible = true;
        }
        else
        {
            _parameter.IsVisible = false;
        }

        AutomationProperties.SetName(this, DescriptionView.TextOf(block));
    }
}
