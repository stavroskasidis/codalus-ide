using System.Globalization;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.WinForms;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// M41b's half of the Windows Forms designer's document (ADR 0073): the form's resources and the language it's
/// designed in, undo across the designer file and the resources, the Properties window's pickers, extender
/// properties, data bindings, images and collections, the component tray's arrangement, the Format menu's commands,
/// tab order mode, smart tags, collection editors, and menu and tool strip editing on the surface.
/// </summary>
public sealed partial class WinFormsDesignerDocument
{
    /// <summary>The languages a localizable form's Language offers besides its own cultures' (the classic IDE lists every culture; these are the common ones).</summary>
    public static IReadOnlyList<string> CommonCultures { get; } =
        ["ar", "cs", "da", "de", "el", "en", "en-GB", "en-US", "es", "fi", "fr", "fr-CA", "he", "hu", "it", "ja", "ko", "nl", "no", "pl", "pt", "pt-BR", "ru", "sv", "tr", "uk", "zh-Hans", "zh-Hant"];

    private string _culture = string.Empty;
    private bool _tabOrderMode;
    private readonly Dictionary<string, int> _tabCounters = new(StringComparer.Ordinal);
    private DesignerActionPanelViewModel? _actionPanel;
    private DesignerCollectionEditorViewModel? _collectionEditor;
    private DesignerFontEditorViewModel? _fontEditor;
    private DesignerStringsEditorViewModel? _stringsEditor;
    private DesignerResourcePickerViewModel? _resourcePicker;
    private IReadOnlyList<DesignerMenuPanel> _menuPanels = [];
    private DesignerTypeHere? _stripTypeHere;

    internal ShellHost Shell => _shell;

    // ------------------------------------------------------------------ the form's resources and its language

    /// <summary>The language a localizable form is designed in (empty: the default resources).</summary>
    public string Culture => _culture;

    /// <summary>The form is localizable (its default resources say so).</summary>
    public bool IsLocalizable => _read?.IsLocalizable ?? false;

    /// <summary>The form's resource files' text models, held while the designer is open (the default one and one per culture).</summary>
    public IReadOnlyList<CodeDocument> ResourceModels => _resourceModels;

    /// <summary>The form's .resx files as the designer holds them (unsaved edits included), which the C# host reads instead of the disk's.</summary>
    private DesignerFileText[]? HeldResourceFiles() =>
        _resourceModels.Count == 0 ? null : [.. _resourceModels.Select(m => new DesignerFileText(m.Path, m.Text.Text))];

    /// <summary>A resource file's culture: <c>fr</c> for <c>OrderForm.fr.resx</c>, empty for <c>OrderForm.resx</c>.</summary>
    private string CultureOf(string path)
    {
        var stem = CodePath[..^".cs".Length];
        return path.Length > stem.Length + ".resx".Length ? path[(stem.Length + 1)..^".resx".Length] : string.Empty;
    }

    /// <summary>Holds the form's resource files' text models (new ones as they appear), so the designer's edits of them are undoable and seen by the designer host.</summary>
    private async Task HoldResourcesAsync()
    {
        foreach (var (_, path) in WinFormsDesignerService.FormResources(CodePath))
        {
            if (_resourceModels.Any(m => SolutionPaths.Comparer.Equals(m.Path, path)))
            {
                continue;
            }

            try
            {
                var model = await _shell.AcquireModelAsync(path).ConfigureAwait(true);
                _resourceModels.Add(model);
                _expectedResources[model] = model.Version;
                model.ContentChanged += OnResourcesChanged;
                model.PropertyChanged += OnDesignerCodePropertyChanged;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _shell.WinFormsDesignerLog.AppendLine($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    /// <summary>The resources changed otherwise (an undo, the resource editor): the form loads again from them.</summary>
    private void OnResourcesChanged(object? sender, CodeDocumentChangedEventArgs e)
    {
        if (sender is CodeDocument model && _expectedResources.TryGetValue(model, out var expected) && e.Version == expected)
        {
            return;
        }

        ScheduleReload();
    }

    private void ReleaseResources()
    {
        foreach (var model in _resourceModels)
        {
            model.ContentChanged -= OnResourcesChanged;
            model.PropertyChanged -= OnDesignerCodePropertyChanged;
            _shell.ReleaseModel(model);
        }

        _resourceModels.Clear();
    }

    /// <summary>The languages the form can be designed in: the default one, its cultures, and the common ones.</summary>
    public IReadOnlyList<string> LanguageChoices =>
    [
        DefaultLanguage,
        .. (_read?.Cultures ?? []).Concat(CommonCultures).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase),
    ];

    private static string DefaultLanguage => Localizer.Get("msg.winFormsDesigner.defaultLanguage", "(Default)");

    /// <summary>
    /// Designs a localizable form in another language (the classic IDE's Language): the form loads again with that
    /// culture's resources, and its localizable properties are written to <c>Form.culture.resx</c> (only what differs).
    /// </summary>
    public async Task SetCultureAsync(string culture)
    {
        culture = culture == DefaultLanguage ? string.Empty : culture.Trim();
        if (culture == _culture)
        {
            return;
        }

        if (culture.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(culture, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$"))
        {
            Status = Localizer.Format("msg.winFormsDesigner.badLanguage", "{0} isn't a culture's name (fr, fr-CA, zh-Hans).", culture);
            return;
        }

        _culture = culture;
        OnPropertyChanged(nameof(Culture));
        await ReloadAsync(restartHost: false).ConfigureAwait(true);
        PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ undo across files

    /// <summary>The held text models an edit changes.</summary>
    private List<CodeDocument> TouchedModels(WorkspaceEdit edit)
    {
        var touched = new List<CodeDocument>();
        foreach (var change in edit.DocumentChanges)
        {
            if (DocumentUris.ToPath(change.TextDocument.Uri) is not { } path)
            {
                continue;
            }

            if (_designerCode is { } designer && SolutionPaths.Comparer.Equals(designer.Path, path))
            {
                touched.Add(designer);
            }
            else if (_resourceModels.FirstOrDefault(m => SolutionPaths.Comparer.Equals(m.Path, path)) is { } resources)
            {
                touched.Add(resources);
            }
        }

        return touched;
    }

    /// <summary>A change that touched the resources too: undone and redone in all of them as one step.</summary>
    private void RecordUndo(List<CodeDocument> touched)
    {
        _redo.Clear();
        if (touched.Count > 1 || touched.Any(m => m != _designerCode))
        {
            _undo.Add([.. touched.Select(m => (m, m.Version))]);
        }
        else
        {
            _undo.Clear();
        }
    }

    /// <summary>Undoes (or redoes) the last change that touched several files, when each of them is still where that change left it.</summary>
    private bool Step(List<(CodeDocument Model, int Version)[]> from, List<(CodeDocument Model, int Version)[]> to, bool undo)
    {
        if (from.Count == 0)
        {
            return false;
        }

        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        if (step.Any(s => s.Model.Version != s.Version || (undo ? !s.Model.Text.UndoStack.CanUndo : !s.Model.Text.UndoStack.CanRedo)))
        {
            from.Clear();
            return false;
        }

        foreach (var (model, _) in step)
        {
            if (undo)
            {
                model.Text.UndoStack.Undo();
            }
            else
            {
                model.Text.UndoStack.Redo();
            }
        }

        to.Add([.. step.Select(s => (s.Model, s.Model.Version))]);
        return true;
    }

    // ------------------------------------------------------------------ the Properties window's rows (M41b)

    /// <summary>
    /// A property's row (#68: as the classic IDE's grid shows it): its parts (<see cref="DesignerPropertyInfo.SubProperties"/>,
    /// each edited through its dotted path), Reset when the host can reset it, its editor from the host's kind (the
    /// pickers, choices that may take typing, flags, colors, alignment, multi-line text, shortcut keys, dates, fonts,
    /// strings, a collection's or an image's editor behind "…"; a Windows Forms dialog's value typed in the cell or
    /// read-only), choices kept for any editor that has standard values (AcceptButton, Font.Name, Cursor); an extender
    /// property as "ToolTip on toolTip1"; a data binding under (DataBindings); the root's Localizable, which moves its
    /// values between the code and the resources. An editor kind this IDE doesn't know falls to text, so an older or newer
    /// host still works.
    /// </summary>
    private PropertyRowViewModel RowFor(List<string> selected, DesignerPropertyInfo info, string value, string? path = null)
    {
        path ??= info.Name;
        var description = info.Localizable
            ? Localizer.Get("msg.winFormsDesigner.localizedProperty", "Localizable: this value is in the form's resources, for the language shown.") + " " + info.Description
            : info.Description ?? string.Empty;
        if (info.Provider is { } provider)
        {
            Func<string?, Task>? setExtender = info.IsReadOnly ? null : text => SetExtenderAsync(selected, info.Name, provider, text ?? string.Empty);

            // Reset clears it, as an empty value does (the host resets an extender property set to nothing). A Text row
            // whose value has a line break opens the multi-line drop-down (D41 question 16).
            Func<Task>? resetExtender = info.IsReadOnly ? null : () => SetExtenderAsync(selected, info.Name, provider, string.Empty);
            var extender = info.Editor is DesignerEditors.Boolean or DesignerEditors.Enum && info.StandardValues is { Length: > 0 } extenderValues
                ? new PropertyRowViewModel(ExtenderName(info.Name, provider), value, PropertyRowKind.Choice, description, setExtender, [.. extenderValues.Select(v => new PropertyOption(v, v))], info.IsSet)
                {
                    Path = $"{info.Name} on {provider}",
                    IsMixed = info.Mixed,
                    Reset = resetExtender,
                    CanReset = info.IsSet || info.CanReset,
                }
                : new PropertyRowViewModel(ExtenderName(info.Name, provider), value, info.Editor == DesignerEditors.Multiline ? PropertyRowKind.Multiline : PropertyRowKind.Text, description, setExtender, null, info.IsSet)
                {
                    Path = $"{info.Name} on {provider}",
                    IsMixed = info.Mixed,
                    Reset = resetExtender,
                    CanReset = info.IsSet || info.CanReset,
                };
            return extender;
        }

        if (info.DataBinding)
        {
            return new PropertyRowViewModel(info.Name, value, PropertyRowKind.Choice, description, text => SetDataBindingAsync(selected, info.Name, text ?? "(none)"),
                [.. (info.StandardValues ?? ["(none)"]).Select(v => new PropertyOption(v, v))], info.IsSet)
            {
                Path = DataBindingsRow + "." + info.Name,
                Reset = () => SetDataBindingAsync(selected, info.Name, "(none)"),
                CanReset = info.IsSet,
                IsMixed = info.Mixed,
            };
        }

        Func<string?, Task>? commit = info.IsReadOnly ? null : text => SetPropertyAsync(selected, path, text ?? string.Empty);
        List<PropertyRowViewModel> children = [.. (info.SubProperties ?? []).Select(sub => RowFor(selected, sub, sub.Mixed ? string.Empty : sub.Value ?? string.Empty, path + "." + sub.Name))];
        List<PropertyOption>? options = info.StandardValues is { Length: > 0 } values ? [.. values.Select(v => new PropertyOption(v, v))] : null;
        System.Windows.Input.ICommand? modal = null;
        PropertyRowKind kind;
        switch (info.Editor)
        {
            case DesignerEditors.Image when selected.Count == 1:
                kind = PropertyRowKind.ReadOnly;
                // Select Resource behind "…": a local resource or one of the project's resources file.
                modal = new RelayCommand(() => _ = OpenResourcePickerAsync(selected[0], info.Name));
                break;
            case DesignerEditors.Collection when selected.Count == 1 && info.Collection is { } key:
                // A collection: "(Collection)", read-only, its editor behind "…" (D41 question 20).
                kind = PropertyRowKind.ReadOnly;
                modal = new RelayCommand(() => _ = OpenCollectionEditorAsync(ComponentOf(selected[0]), key));
                break;
            case DesignerEditors.Dock when options is not null:
                kind = PropertyRowKind.Dock;
                break;
            case DesignerEditors.Anchor when options is not null:
                kind = PropertyRowKind.Anchor;
                options = [.. options.Where(o => o.Value is not "None")];
                break;
            case DesignerEditors.Flags when options is not null:
                kind = PropertyRowKind.Flags;
                options = [.. options.Where(o => o.Value is not "None")];
                break;
            case DesignerEditors.Color:
                kind = PropertyRowKind.Color;
                break;
            case DesignerEditors.ContentAlignment:
                kind = PropertyRowKind.ContentAlignment;
                break;
            case DesignerEditors.Multiline:
                kind = PropertyRowKind.Multiline;
                break;
            case DesignerEditors.ShortcutKeys:
                kind = PropertyRowKind.ShortcutKeys;
                break;
            case DesignerEditors.Date:
                kind = PropertyRowKind.Date;
                break;
            case DesignerEditors.Font:
                kind = PropertyRowKind.Font;
                modal = info.IsReadOnly ? null : new RelayCommand(() => OpenFontEditor(selected, path, info));
                break;
            case DesignerEditors.Strings:
                // A string array (a text box's Lines): the String Collection Editor behind "…".
                kind = PropertyRowKind.ReadOnly;
                modal = info.IsReadOnly ? null : new RelayCommand(() => _ = OpenStringsEditorAsync(selected, path));
                break;
            case DesignerEditors.Modal:
                // A Windows Forms dialog the IDE doesn't show (D41 question 8): no "…"; typed in the cell when its converter takes text.
                // TODO(issue): component editors (a control's Property Pages) and the data grid view's cell style builder as the
                // IDE's own panels (#68, D41 question 8); their values are typed here or set in the code meanwhile.
                kind = PropertyRowKind.Text;
                description = (description.Length == 0 ? string.Empty : description + "\n") + (info.IsReadOnly
                    ? Localizer.Get("msg.winFormsDesigner.dialogEditorReadOnly", "Its editor is a Windows Forms dialog, which Codalus IDE doesn't show, and the value can't be typed: set it in the code.")
                    : Localizer.Get("msg.winFormsDesigner.dialogEditor", "Its editor is a Windows Forms dialog, which Codalus IDE doesn't show: type the value here."));
                break;
            case DesignerEditors.Boolean or DesignerEditors.Enum when options is not null:
                kind = PropertyRowKind.Choice;
                break;
            default:
                // Any other editor with standard values is a choice (AcceptButton, Font.Name, Cursor), typed too when the list isn't exclusive.
                kind = options is not null ? PropertyRowKind.Choice : PropertyRowKind.Text;
                break;
        }

        return new PropertyRowViewModel(info.Name, value, kind, description, commit, options, info.IsSet)
        {
            Path = path,
            Children = children,
            Reset = info.IsReadOnly && info.SubProperties is null ? null : () => SetPropertyAsync(selected, path, null),
            CanReset = info.CanReset,
            ChoicesEditable = info.ChoicesEditable,
            IsMixed = info.Mixed,
            ModalEditor = modal,
            IsCursor = info.Type == "System.Windows.Forms.Cursor",
        };
    }

    /// <summary>The row the shell nests the data bindings under (#68, D41 question 6), in Data, as the classic IDE's.</summary>
    internal const string DataBindingsRow = "(DataBindings)";

    /// <summary>The classic IDE's name of an extender property's row: <c>ToolTip on toolTip1</c>.</summary>
    public static string ExtenderName(string property, string provider) => Localizer.Format("msg.winFormsDesigner.extender", "{0} on {1}", property, provider);

    /// <summary>The root's Language row of a localizable form (the designer's, not the code's).</summary>
    private DesignerPropertyRow LanguageRow() =>
        new("Design", new PropertyRowViewModel("Language", _culture.Length == 0 ? DefaultLanguage : _culture, PropertyRowKind.Choice,
            Localizer.Get("msg.winFormsDesigner.language", "The language the form is designed in: its localizable properties are written to that language's resources (only what differs from the default)."),
            text => SetCultureAsync(text ?? DefaultLanguage), [.. LanguageChoices.Select(c => new PropertyOption(c, c))], _culture.Length > 0));

    /// <summary>An extender property set in the Properties window (<c>ToolTip on toolTip1</c>), or reset when empty; the host's error, or null.</summary>
    public Task<string?> SetExtenderAsync(IReadOnlyList<string> components, string property, string provider, string text) =>
        RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. components.Select(c => text.Length == 0
            ? new DesignerPropertyEdit(ComponentOf(c), property) { Provider = provider, Reset = true }
            : new DesignerPropertyEdit(ComponentOf(c), property) { Provider = provider, Text = text })]), CancellationToken.None));

    /// <summary>A property bound to a binding source's member (<c>bindingSource1 - Name</c>) or unbound (<c>(none)</c>); the host's error, or null.</summary>
    public Task<string?> SetDataBindingAsync(IReadOnlyList<string> components, string property, string binding) =>
        RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. components.Select(c => new DesignerPropertyEdit(ComponentOf(c), property) { DataBinding = true, Text = binding })]), CancellationToken.None));

    // ------------------------------------------------------------------ images (M41b)

    /// <summary>
    /// An image imported from a file for an image property: into the form's resources (read with <c>GetObject</c>, or
    /// with ApplyResources in a localizable form), or into the project's resources (<c>Resources/</c> and
    /// <c>Properties/Resources.resx</c>, read as <c>Properties.Resources.Name</c>), as the classic IDE's Select Resource offers.
    /// </summary>
    public async Task ImportImageAsync(string component, string property, string? path, bool toProject)
    {
        path ??= _shell.Window is { } window
            ? await window.PickFileAsync(Localizer.Get("msg.winFormsDesigner.pickImage", "Import an Image"), "Images", ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.ico"]).ConfigureAwait(true)
            : null;
        if (path is null)
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            return;
        }

        if (!toProject)
        {
            await RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit(ComponentOf(component), property) { Bytes = Convert.ToBase64String(bytes) }]), CancellationToken.None)).ConfigureAwait(true);
            return;
        }

        await ImportIntoProjectAsync(component, property, path, bytes).ConfigureAwait(true);
    }

    private async Task ImportIntoProjectAsync(string component, string property, string path, byte[] bytes)
    {
        if (ProjectResxPath() is not { } resx)
        {
            Status = Localizer.Get("msg.winFormsDesigner.noProjectResources", "The project has no Properties/Resources.resx: add a resources file there first, or import the image into the form's resources.");
            return;
        }

        var name = ResourceNameOf(path);
        var project = Path.GetDirectoryName(Path.GetDirectoryName(resx))!;
        var target = Path.Combine(project, "Resources", name + Path.GetExtension(path).ToLowerInvariant());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!SolutionPaths.Comparer.Equals(Path.GetFullPath(path), target))
            {
                await File.WriteAllBytesAsync(target, bytes).ConfigureAwait(true);
            }

            // The project's resources: a file reference, as the classic IDE adds it; its strongly typed class generated again.
            var text = await File.ReadAllTextAsync(resx).ConfigureAwait(true);
            var type = Path.GetExtension(target).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? "System.Drawing.Icon, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
                : "System.Drawing.Bitmap, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
            if (!text.Contains($"<data name=\"{name}\"", StringComparison.Ordinal))
            {
                var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var entry = $"  <assembly alias=\"System.Windows.Forms\" name=\"System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089\" />{newLine}"
                    + $"  <data name=\"{name}\" type=\"System.Resources.ResXFileRef, System.Windows.Forms\">{newLine}"
                    + $"    <value>..\\Resources\\{Path.GetFileName(target)};{type}</value>{newLine}  </data>{newLine}";
                if (text.Contains("<assembly alias=\"System.Windows.Forms\"", StringComparison.Ordinal))
                {
                    entry = entry[(entry.IndexOf(newLine, StringComparison.Ordinal) + newLine.Length)..];
                }

                var close = text.LastIndexOf("</root>", StringComparison.Ordinal);
                await File.WriteAllTextAsync(resx, text[..close] + entry + text[close..]).ConfigureAwait(true);
            }

            await _shell.RunCustomToolAsync(resx, quiet: true).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
            return;
        }

        await UseProjectResourceAsync(component, property, name, bytes).ConfigureAwait(true);
    }

    /// <summary>A project resource's name for an image file: its name with what an identifier can't hold as "_".</summary>
    internal static string ResourceNameOf(string path)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(path), "[^A-Za-z0-9_]", "_");
        return name.Length == 0 ? "_" : char.IsDigit(name[0]) ? "_" + name : name;
    }

    /// <summary>An image of the project's resources for an image property: the designer shows its bytes, the code says <c>Properties.Resources.Name</c> (#68: Select Resource).</summary>
    internal async Task UseProjectResourceAsync(string component, string property, string name, byte[] bytes)
    {
        var rootNamespace = _read?.ClassFullName is { } full && full.Contains('.', StringComparison.Ordinal) ? full[..full.IndexOf('.', StringComparison.Ordinal)] : _model?.Namespace;
        var member = DesignerValue.MemberOf((rootNamespace is null ? string.Empty : rootNamespace + ".") + "Properties.Resources", name);
        await RunAsync(async host =>
        {
            var result = await host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit(ComponentOf(component), property) { Bytes = Convert.ToBase64String(bytes) }]), CancellationToken.None).ConfigureAwait(true);
            return result with
            {
                Changes = [.. result.Changes.Where(c => c.Property != property || ComponentOf(c.Component) != ComponentOf(component)), new DesignerPropertyChange(ComponentOf(component), property, member)],
                Resources = [.. result.Resources.Where(r => r.Key != (ComponentOf(component).Length == 0 ? "$this" : ComponentOf(component)) + "." + property)],
            };
        }).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ the component tray (M41b)

    /// <summary>The tray drawn again (a rename starting or ending).</summary>
    public void RefreshTray() => UpdateTray();

    /// <summary>A tray component double-clicked: its default event's handler (a timer's Tick), as the classic IDE does.</summary>
    public Task DefaultEventOfAsync(string component) =>
        Find(component) is { } view ? HookEventAsync(component, view.DefaultEvent ?? "Disposed", handler: null) : Task.CompletedTask;

    /// <summary>A tray component's icon: its Toolbox category's.</summary>
    private static string TrayIcon(string type)
    {
        var name = type[(type.LastIndexOf('.') + 1)..];
        return name.EndsWith("Strip", StringComparison.Ordinal) ? "list"
            : name.EndsWith("Dialog", StringComparison.Ordinal) ? "window"
            : name is "BindingSource" or "BindingNavigator" ? "table"
            : name is "PrintDocument" ? "file"
            : "gear";
    }

    /// <summary>
    /// Arranges the tray: its components in this order (Line Up Icons, or one dragged before another), recorded as the
    /// classic IDE does, in the default resources' <c>TrayLocation</c> metadata.
    /// </summary>
    public Task ArrangeTrayAsync(IReadOnlyList<string> order)
    {
        if (_model is null || order.Count == 0)
        {
            return Task.CompletedTask;
        }

        var edits = new List<DesignerEdit>();
        for (var i = 0; i < order.Count; i++)
        {
            var point = string.Create(CultureInfo.InvariantCulture, $"{17 + ((i % 6) * 125)}, {17 + ((i / 6) * 39)}");
            edits.Add(new DesignerEdit
            {
                Kind = DesignerEditKinds.SetResource,
                Key = order[i] + ".TrayLocation",
                Metadata = true,
                Resource = new DesignerResource { Type = "System.Drawing.Point, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", Text = point },
            });
        }

        return WriteGatedAsync(edits, reloadAfter: true);
    }

    /// <summary>Line Up Icons: the tray in the code's order of creation.</summary>
    public Task LineUpTrayAsync() =>
        ArrangeTrayAsync([.. Tray.Select(t => t.Name).OrderBy(n => _model?.Components.ToList().FindIndex(c => c.Name == n) ?? 0)]);

    /// <summary>A tray component moved before another (or to the end).</summary>
    public Task MoveTrayItemAsync(string name, string? before)
    {
        var order = Tray.Select(t => t.Name).Where(n => n != name).ToList();
        var at = before is null ? order.Count : Math.Max(0, order.IndexOf(before));
        order.Insert(at < 0 ? order.Count : at, name);
        return ArrangeTrayAsync(order);
    }

    private async Task WriteGatedAsync(IReadOnlyList<DesignerEdit> edits, bool reloadAfter)
    {
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            await WriteAsync(edits, reloadAfter, showHandler: false).ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ the Format menu (M41b)

    /// <summary>
    /// What the Format menu lines up: the selected controls (not the form, not parts) with the primary selection's parent,
    /// the primary first. A locked primary stays the reference, as in the classic IDE (#67: Align and Make Same Size read
    /// its bounds); the commands move and size only the unlocked ones (<see cref="Unlocked"/>).
    /// </summary>
    private (DesignerComponentView? Primary, List<DesignerComponentView> Group) Formatted()
    {
        var selected = Orderable();
        if (selected.Count == 0)
        {
            return (null, selected);
        }

        var primary = selected.FirstOrDefault(m => m.Name == PrimarySelection) ?? selected[0];
        return (primary, [primary, .. selected.Where(m => m != primary && m.Parent == primary.Parent)]);
    }

    /// <summary>The selected controls Bring to Front and Send to Back take (not the form, not parts; locked ones too, as in the classic IDE).</summary>
    private List<DesignerComponentView> Orderable() =>
        [.. _selection.Select(Find).OfType<DesignerComponentView>().Where(c => c.IsControl && c != Root && !c.IsPart)];

    /// <summary>The ones a Format command moves or sizes: the classic IDE skips locked controls.</summary>
    private static IEnumerable<DesignerComponentView> Unlocked(IEnumerable<DesignerComponentView> views) => views.Where(v => v.Locked != true);

    private DesignerPropertyEdit LocationEdit(DesignerComponentView view, double x, double y)
    {
        var parent = view.Parent is { } p ? Find(p) : null;
        return new DesignerPropertyEdit(view.Name, "Location") { Text = $"{Invariant(x - (parent?.ClientX ?? 0))}, {Invariant(y - (parent?.ClientY ?? 0))}" };
    }

    private static DesignerPropertyEdit SizeEdit(DesignerComponentView view, double width, double height) =>
        new(view.Name, "Size") { Text = $"{Invariant(Math.Max(1, width))}, {Invariant(Math.Max(1, height))}" };

    private Task Apply(IEnumerable<DesignerPropertyEdit> edits)
    {
        var list = edits.ToList();
        return list.Count == 0 ? Task.CompletedTask : RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([.. list]), CancellationToken.None));
    }

    /// <summary>Format → Align: to the primary selection's left, center, right, top, middle or bottom, or each to the grid (8 pixels).</summary>
    public Task AlignAsync(DesignerAlignment alignment)
    {
        var (primary, views) = Formatted();
        if (primary is null || (views.Count < 2 && alignment != DesignerAlignment.ToGrid))
        {
            return Task.CompletedTask;
        }

        var edits = new List<DesignerPropertyEdit>();
        foreach (var view in Unlocked(alignment == DesignerAlignment.ToGrid ? views : views.Skip(1)))
        {
            var parent = view.Parent is { } p ? Find(p) : null;
            double x = view.X, y = view.Y;
            switch (alignment)
            {
                case DesignerAlignment.Lefts: x = primary.X; break;
                case DesignerAlignment.Centers: x = primary.X + (primary.Width / 2) - (view.Width / 2); break;
                case DesignerAlignment.Rights: x = primary.X + primary.Width - view.Width; break;
                case DesignerAlignment.Tops: y = primary.Y; break;
                case DesignerAlignment.Middles: y = primary.Y + (primary.Height / 2) - (view.Height / 2); break;
                case DesignerAlignment.Bottoms: y = primary.Y + primary.Height - view.Height; break;
                case DesignerAlignment.ToGrid:
                    var originX = parent?.ClientX ?? 0;
                    var originY = parent?.ClientY ?? 0;
                    x = originX + (Math.Round((view.X - originX) / GridSize, MidpointRounding.AwayFromZero) * GridSize);
                    y = originY + (Math.Round((view.Y - originY) / GridSize, MidpointRounding.AwayFromZero) * GridSize);
                    break;
            }

            if (Math.Abs(x - view.X) >= 0.5 || Math.Abs(y - view.Y) >= 0.5)
            {
                edits.Add(LocationEdit(view, x, y));
            }
        }

        return Apply(edits);
    }

    /// <summary>The designer's grid, as the classic IDE's default (Align to Grid, Size to Grid, spacing steps).</summary>
    public const double GridSize = 8;

    /// <summary>Format → Make Same Size: the primary selection's width, height or both; or each sized to the grid.</summary>
    public Task MakeSameSizeAsync(DesignerSizing sizing)
    {
        var (primary, views) = Formatted();
        if (primary is null || (views.Count < 2 && sizing != DesignerSizing.ToGrid))
        {
            return Task.CompletedTask;
        }

        return Apply(Unlocked(sizing == DesignerSizing.ToGrid ? views : views.Skip(1)).Select(view => sizing switch
        {
            DesignerSizing.Width => SizeEdit(view, primary.Width, view.Height),
            DesignerSizing.Height => SizeEdit(view, view.Width, primary.Height),
            DesignerSizing.Both => SizeEdit(view, primary.Width, primary.Height),
            _ => SizeEdit(view, Math.Max(GridSize, Math.Round(view.Width / GridSize, MidpointRounding.AwayFromZero) * GridSize), Math.Max(GridSize, Math.Round(view.Height / GridSize, MidpointRounding.AwayFromZero) * GridSize)),
        }));
    }

    /// <summary>
    /// Format → Horizontal or Vertical Spacing: make equal, increase or decrease by the grid, or remove, the first control
    /// staying; laid out over every selected control, a locked one kept where it is (#67, as the classic IDE).
    /// </summary>
    public Task SpacingAsync(bool horizontal, DesignerSpacing spacing)
    {
        var (_, views) = Formatted();
        if (views.Count < 2 || (spacing == DesignerSpacing.MakeEqual && views.Count < 3))
        {
            return Task.CompletedTask;
        }

        double Start(DesignerComponentView v) => horizontal ? v.X : v.Y;
        double Length(DesignerComponentView v) => horizontal ? v.Width : v.Height;
        var sorted = views.OrderBy(Start).ToList();
        var edits = new List<DesignerPropertyEdit>();
        var gap = spacing switch
        {
            DesignerSpacing.MakeEqual => (Start(sorted[^1]) + Length(sorted[^1]) - Start(sorted[0]) - sorted.Sum(Length)) / (sorted.Count - 1),
            DesignerSpacing.Remove => 0,
            _ => double.NaN,
        };
        var position = Start(sorted[0]) + Length(sorted[0]);
        for (var i = 1; i < sorted.Count; i++)
        {
            var view = sorted[i];
            var current = Start(view) - (Start(sorted[i - 1]) + Length(sorted[i - 1]));
            var target = double.IsNaN(gap) ? Math.Max(0, current + (spacing == DesignerSpacing.Increase ? GridSize : -GridSize)) : gap;
            var start = position + target;
            position = start + Length(view);
            if (Math.Abs(start - Start(view)) >= 0.5 && view.Locked != true)
            {
                edits.Add(horizontal ? LocationEdit(view, Math.Round(start), view.Y) : LocationEdit(view, view.X, Math.Round(start)));
            }
        }

        return Apply(edits);
    }

    /// <summary>Format → Center in Form: the selection's unlocked controls (as a group) centered in their parent's client area, horizontally or vertically.</summary>
    public Task CenterAsync(bool horizontal)
    {
        var views = Unlocked(Formatted().Group).ToList();
        if (views.Count == 0 || (views[0].Parent is { } p ? Find(p) : Root) is not { } parent)
        {
            return Task.CompletedTask;
        }

        // The client area: inside the parent's border (as wide at the bottom and right as at the left).
        var border = parent.ClientX - parent.X;
        var clientWidth = parent.Width - (2 * border);
        var clientHeight = parent.Height - (parent.ClientY - parent.Y) - border;
        var left = views.Min(v => v.X);
        var top = views.Min(v => v.Y);
        var right = views.Max(v => v.X + v.Width);
        var bottom = views.Max(v => v.Y + v.Height);
        var dx = horizontal ? parent.ClientX + ((clientWidth - (right - left)) / 2) - left : 0;
        var dy = horizontal ? 0 : parent.ClientY + ((clientHeight - (bottom - top)) / 2) - top;
        return Apply(views.Where(_ => Math.Abs(dx) >= 0.5 || Math.Abs(dy) >= 0.5).Select(v => LocationEdit(v, Math.Round(v.X + dx), Math.Round(v.Y + dy))));
    }

    /// <summary>Format → Order: Bring to Front (the first of its parent's controls) or Send to Back (the last), written as <c>Controls.Add</c>'s order.</summary>
    public async Task OrderAsync(bool front)
    {
        // TODO(issue): one undo step for two or more controls brought to the front or sent to the back (one each today).
        var views = Orderable();
        foreach (var view in front ? Enumerable.Reverse(views) : views)
        {
            await RunAsync(host => host.SetChildIndexAsync(new DesignerChildIndexParams(view.Name, front ? 0 : int.MaxValue), CancellationToken.None)).ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------------ tab order mode (M41b)

    /// <summary>View → Tab Order: the controls' tab indexes over them; clicks set the sequence.</summary>
    public bool IsTabOrderMode
    {
        get => _tabOrderMode;
        set
        {
            if (SetProperty(ref _tabOrderMode, value))
            {
                _tabCounters.Clear();
                Status = value
                    ? Localizer.Get("msg.winFormsDesigner.tabOrder", "Tab order: click the controls in the order the Tab key visits them (each container counts from 0). Esc or View → Tab Order ends it.")
                    : Localizer.Format("msg.winFormsDesigner.ready", "{0}: drag from the Toolbox, select, move and resize; F7 shows the code.", _model?.ClassName ?? string.Empty);
                SurfaceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>A control's tab order label as the classic IDE shows it: its index, after its containers' (<c>2.1</c>).</summary>
    public string? TabOrderLabel(DesignerComponentView view)
    {
        if (view.TabIndex is not { } index || view == Root)
        {
            return null;
        }

        var labels = new List<string> { index.ToString(CultureInfo.InvariantCulture) };
        for (var parent = view.Parent is { } p ? Find(p) : null; parent is not null && parent != Root; parent = parent.Parent is { } next ? Find(next) : null)
        {
            if (parent.TabIndex is { } parentIndex)
            {
                labels.Insert(0, parentIndex.ToString(CultureInfo.InvariantCulture));
            }
        }

        return string.Join('.', labels);
    }

    /// <summary>A click in tab order mode: the control under it gets the next tab index of its container.</summary>
    public Task TabOrderClickAsync(double x, double y)
    {
        if (HitTest(x, y) is not { IsControl: true, IsPart: false } hit || hit == Root)
        {
            return Task.CompletedTask;
        }

        var parent = hit.Parent ?? string.Empty;
        var next = _tabCounters.GetValueOrDefault(parent);
        _tabCounters[parent] = next + 1;
        return RunAsync(host => host.SetPropertiesAsync(new DesignerSetPropertiesParams([new DesignerPropertyEdit(hit.Name, "TabIndex") { Text = next.ToString(CultureInfo.InvariantCulture) }]), CancellationToken.None));
    }

    // ------------------------------------------------------------------ smart tags (M41b)

    /// <summary>The open smart tag's panel, or null.</summary>
    public DesignerActionPanelViewModel? ActionPanel
    {
        get => _actionPanel;
        private set => SetProperty(ref _actionPanel, value);
    }

    /// <summary>Opens a component's smart tag: its designer actions from the designer host.</summary>
    public async Task OpenActionsAsync(string component)
    {
        if (_host is not { } host)
        {
            return;
        }

        try
        {
            var actions = await host.ActionsAsync(new DesignerComponentsParams([ComponentOf(component)]), CancellationToken.None).ConfigureAwait(true);
            ActionPanel = new DesignerActionPanelViewModel(this, component, actions.Items);
        }
        catch (Exception ex) when (ex is PreviewerException or Rpc.JsonRpcConnectionLostException or Rpc.JsonRpcRemoteException)
        {
            Status = ex.Message;
        }
    }

    public void CloseActions() => ActionPanel = null;

    /// <summary>A smart tag's item: a property set, a method run (its changes written), or the IDE's editor for what a dialog would edit.</summary>
    internal async Task InvokeActionAsync(string component, Workbench.WinForms.DesignerActionItem item, string? text)
    {
        if (item.Collection is { } key)
        {
            CloseActions();
            await OpenCollectionEditorAsync(ComponentOf(component), key).ConfigureAwait(true);
            return;
        }

        if (item.ImageProperty is { } property)
        {
            CloseActions();
            await ImportImageAsync(component, property, path: null, toProject: false).ConfigureAwait(true);
            return;
        }

        await RunAsync(host => host.InvokeActionAsync(new DesignerInvokeActionParams(ComponentOf(component), item.Member) { Text = text }, CancellationToken.None)).ConfigureAwait(true);
        if (_actionPanel is { } panel && panel.Component == component && Find(component) is not null)
        {
            await OpenActionsAsync(component).ConfigureAwait(true);
        }
        else
        {
            CloseActions();
        }
    }

    // ------------------------------------------------------------------ collection editors (M41b)

    /// <summary>The open collection editor, or null.</summary>
    public DesignerCollectionEditorViewModel? CollectionEditor
    {
        get => _collectionEditor;
        private set
        {
            if (SetProperty(ref _collectionEditor, value))
            {
                // The Properties window takes no input while the editor is open, and reads the values again when it closes (#68).
                PropertiesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    // ------------------------------------------------------------------ the font editor and the String Collection Editor (#68)

    /// <summary>The open font editor, or null: a panel over the surface (and over the collection editor, when opened from its item grid).</summary>
    public DesignerFontEditorViewModel? FontEditor
    {
        get => _fontEditor;
        private set
        {
            if (SetProperty(ref _fontEditor, value))
            {
                OnEditorPanelChanged();
            }
        }
    }

    /// <summary>The open String Collection Editor, or null.</summary>
    public DesignerStringsEditorViewModel? StringsEditor
    {
        get => _stringsEditor;
        private set
        {
            if (SetProperty(ref _stringsEditor, value))
            {
                OnEditorPanelChanged();
            }
        }
    }

    /// <summary>The open Select Resource, or null.</summary>
    public DesignerResourcePickerViewModel? ResourcePicker
    {
        get => _resourcePicker;
        private set
        {
            if (SetProperty(ref _resourcePicker, value))
            {
                OnEditorPanelChanged();
            }
        }
    }

    /// <summary>A panel over the others (the font editor, the String Collection Editor, Select Resource) is open.</summary>
    public bool HasEditorPanel => _fontEditor is not null || _stringsEditor is not null || _resourcePicker is not null;

    private void OnEditorPanelChanged()
    {
        // The grids under a panel take no input; they read the values again when it closes (D41 question 2).
        _collectionEditor?.Grid.IsEnabled = !HasEditorPanel;
        PropertiesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(HasEditorPanel));
    }

    /// <summary>"placeOrderButton · Font": what a panel edits, as the in-window dialog's context line says it.</summary>
    private string EditorContext(IReadOnlyList<string> selected, string path) =>
        Localizer.Format("msg.winFormsDesigner.editorContext", "{0} · {1}", selected.Count == 1 ? selected[0] : Localizer.Format("msg.winFormsDesigner.selectedCount", "{0} components", selected.Count), path);

    /// <summary>The font editor for a font property, from its parts as the host described them.</summary>
    internal void OpenFontEditor(IReadOnlyList<string> selected, string path, DesignerPropertyInfo font)
    {
        string Part(string name) => font.SubProperties?.FirstOrDefault(p => p.Name == name) is { Mixed: false, Value: { } value } ? value : string.Empty;
        bool Flag(string name) => string.Equals(Part(name), "True", StringComparison.OrdinalIgnoreCase);
        var families = font.SubProperties?.FirstOrDefault(p => p.Name == "Name")?.StandardValues ?? [];
        FontEditor = new DesignerFontEditorViewModel(this, selected, path, EditorContext(selected, path), families, Part("Name"), Part("Size"), Flag("Bold"), Flag("Italic"), Flag("Strikeout"), Flag("Underline"));
    }

    public void CloseFontEditor() => FontEditor = null;

    /// <summary>
    /// The String Collection Editor for a string array: its items from the code's assignment (<c>new string[] { … }</c>),
    /// or, for a text box's Lines, from its Text, as the classic editor shows them.
    /// </summary>
    internal async Task OpenStringsEditorAsync(IReadOnlyList<string> selected, string path)
    {
        var component = ComponentOf(selected[0]);
        string text;
        if (_model?.Statements.LastOrDefault(s => s.Kind == DesignerStatementKinds.Assign && s.Target == component && s.Member == path) is { Value: { Kind: DesignerValueKinds.Array, Arguments: { } items } })
        {
            text = string.Join("\r\n", items.Select(i => i.Value ?? string.Empty));
        }
        else if (path == "Lines" && _host is { } host)
        {
            var properties = await host.PropertiesAsync(new DesignerComponentsParams([component]), CancellationToken.None).ConfigureAwait(true);
            text = properties.Properties.FirstOrDefault(p => p is { Name: "Text", DataBinding: false, Provider: null })?.Value ?? string.Empty;
        }
        else
        {
            text = string.Empty;
        }

        StringsEditor = new DesignerStringsEditorViewModel(this, selected, path, EditorContext(selected, path), PropertyText.WithCrLf(text));
    }

    public void CloseStringsEditor() => StringsEditor = null;

    /// <summary>The project's resources file (<c>Properties/Resources.resx</c> beside the project), or null when it has none.</summary>
    private string? ProjectResxPath()
    {
        var project = _read?.Project is { } projectPath ? Path.GetDirectoryName(projectPath)! : Path.GetDirectoryName(CodePath)!;
        var resx = Path.Combine(project, "Properties", "Resources.resx");
        return File.Exists(resx) ? resx : null;
    }

    /// <summary>
    /// Select Resource for an image property: the project's resources file's images, and the one the code uses
    /// (<c>Properties.Resources.Name</c>) chosen.
    /// </summary>
    internal async Task OpenResourcePickerAsync(string component, string property)
    {
        var resx = ProjectResxPath();
        IReadOnlyList<DesignerProjectResource> images = [];
        if (resx is not null)
        {
            try
            {
                images = DesignerProjectResource.ImagesOf(resx, await File.ReadAllTextAsync(resx).ConfigureAwait(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                Status = ex.Message;
            }
        }

        var target = ComponentOf(component);
        var current = _model?.Statements.LastOrDefault(s => s.Kind == DesignerStatementKinds.Assign && s.Target == target && s.Member == property) is
            { Value: { Kind: DesignerValueKinds.Member, Type: { } type, Value: { } member } } && type.EndsWith("Properties.Resources", StringComparison.Ordinal) ? member : null;
        ResourcePicker = new DesignerResourcePickerViewModel(this, component, property, EditorContext([component], property), resx, images, current);
    }

    public void CloseResourcePicker() => ResourcePicker = null;

    /// <summary>Opens a collection's editor (Items, Columns, TabPages, ColumnStyles, a strip's items, an image list's images).</summary>
    public async Task OpenCollectionEditorAsync(string component, string key)
    {
        if (_host is null || DesignerCollections.Find(key) is not { } info)
        {
            return;
        }

        var editor = new DesignerCollectionEditorViewModel(this, component.Length == 0 ? _model?.ClassName ?? string.Empty : component, info);
        CollectionEditor = editor;
        await editor.RefreshAsync().ConfigureAwait(true);
    }

    public void CloseCollectionEditor()
    {
        CloseFontEditor();
        CloseStringsEditor();
        CloseResourcePicker();
        CollectionEditor = null;
    }

    internal async Task<DesignerCollectionResult?> CollectionItemsAsync(string component, string property)
    {
        if (_host is not { } host)
        {
            return null;
        }

        try
        {
            return await host.CollectionAsync(new DesignerCollectionParams(ComponentOf(component), property), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is PreviewerException or Rpc.JsonRpcConnectionLostException or Rpc.JsonRpcRemoteException)
        {
            Status = ex.Message;
            return null;
        }
    }

    /// <summary>A collection's new items, through the designer host, written as code.</summary>
    internal Task SetCollectionAsync(string component, string property, IReadOnlyList<DesignerCollectionItem> items) =>
        RunAsync(host => host.SetCollectionAsync(new DesignerSetCollectionParams(ComponentOf(component), property, [.. items]), CancellationToken.None));

    /// <summary>An item component's rows (a column, a menu item, a tab page) with their categories, for the collection editor's property grid.</summary>
    internal async Task<IReadOnlyList<DesignerPropertyRow>> ItemRowsAsync(string item)
    {
        if (_host is not { } host)
        {
            return [];
        }

        try
        {
            var result = await host.PropertiesAsync(new DesignerComponentsParams([item]), CancellationToken.None).ConfigureAwait(true);
            return [.. result.Properties.Where(p => p.Provider is null && !p.DataBinding && !(p is { Name: "Locked", Type: "System.Boolean" }))
                .Select(p => new DesignerPropertyRow(p.Category, p.Name == "Name" ? new PropertyRowViewModel("(Name)", p.Value ?? string.Empty, PropertyRowKind.ReadOnly, p.Description ?? string.Empty, isSet: true) { Path = "(Name)" } : RowFor([item], p, p.Mixed ? string.Empty : p.Value ?? string.Empty)))];
        }
        catch (Exception ex) when (ex is PreviewerException or Rpc.JsonRpcConnectionLostException or Rpc.JsonRpcRemoteException)
        {
            Status = ex.Message;
            return [];
        }
    }

    // ------------------------------------------------------------------ menus and tool strips on the surface (M41b)

    /// <summary>The drop-downs open on the surface (a selected menu item's, and its parents'), with their rows and Type Here.</summary>
    public IReadOnlyList<DesignerMenuPanel> MenuPanels => _menuPanels;

    /// <summary>The Type Here box after a selected strip's (or its item's) last item, or null.</summary>
    public DesignerTypeHere? StripTypeHere => _stripTypeHere;

    private const double ItemHeight = 22;
    private const double SeparatorHeight = 8;

    private static double TextWidth(string? text) => Math.Max(24, ((text?.Replace("&", string.Empty, StringComparison.Ordinal).Length ?? 0) * 7) + 16);

    /// <summary>The items of a strip or a menu item, in their order.</summary>
    public IReadOnlyList<DesignerComponentView> ItemsOf(string owner) => [.. _components.Where(c => c.IsItem && c.Owner == owner).OrderBy(c => c.ItemIndex)];

    /// <summary>
    /// The tool strip a component is or is in (through its items' owners), or null. A strip is a component whose
    /// <c>Items</c> hold tool strip items, by the collection table (#67): a list box's, combo box's, domain up-down's or
    /// list view's items are a list's, which take no Type Here; nor does a control whose type name merely ends in "Strip".
    /// </summary>
    private DesignerComponentView? StripOf(string name)
    {
        for (var view = Find(name); view is not null; view = view.Owner is { } owner ? Find(owner) : null)
        {
            if (!view.IsItem)
            {
                return view.Collections?.Any(DesignerCollections.IsStripItems) == true ? view : null;
            }
        }

        return null;
    }

    /// <summary>What a strip's Type Here adds: typed text makes a menu item; a tool strip or status strip picks a type.</summary>
    private static (string Property, string[] Types) ItemsInfo(DesignerComponentView owner)
    {
        var key = owner.Collections?.FirstOrDefault(c => DesignerCollections.IsStripItems(c) || DesignerCollections.Find(c) is { Property: "DropDownItems" });
        var info = key is null ? null : DesignerCollections.Find(key);
        return info is null ? ("Items", [.. DesignerCollections.All.First(c => c.Property == "Items" && c.OwnerType.EndsWith("MenuStrip", StringComparison.Ordinal)).ItemTypes]) : (info.Property, info.ItemTypes);
    }

    /// <summary>The surface's menu adorners for the selection: the strip's Type Here, and the open drop-downs.</summary>
    private void UpdateMenus()
    {
        _menuPanels = [];
        _stripTypeHere = null;
        if (PrimarySelection is not { } selected || StripOf(selected) is not { } strip)
        {
            return;
        }

        var panels = new List<DesignerMenuPanel>();
        if (strip.Visible && strip.Width > 0)
        {
            var items = ItemsOf(strip.Name).Where(i => i.Visible).ToList();
            var x = items.Count == 0 ? strip.X + 2 : items.Max(i => i.X + i.Width) + 2;
            var (property, types) = ItemsInfo(strip);
            _stripTypeHere = new DesignerTypeHere(strip.Name, property, types, new DesignerRect(x, strip.Y + 2, 96, Math.Max(16, strip.Height - 4)),
                CanType: types.Length > 0 && types[0] == DesignerCollections.MenuItemType);
        }

        // The open drop-downs: the selected item's (when it has one) and its owners', from the strip down.
        var chain = new List<DesignerComponentView>();
        for (var view = Find(selected); view is { IsItem: true }; view = view.Owner is { } owner ? Find(owner) : null)
        {
            if (view.HasDropDown)
            {
                chain.Insert(0, view);
            }
        }

        if (!strip.Visible)
        {
            // A context menu: shown at the top of the form when it (or one of its items) is selected, as the classic IDE does.
            panels.Add(Panel(strip, new DesignerRect((Root?.ClientX ?? 0) + 4, (Root?.ClientY ?? 0) + 4, 0, 0), header: strip.Name));
        }

        foreach (var owner in chain)
        {
            var anchor = owner.Visible
                ? new DesignerRect(owner.X, owner.Y + owner.Height, 0, 0)
                : panels.SelectMany(p => p.Rows).FirstOrDefault(r => r.Item == owner.Name) is { } row ? new DesignerRect(row.Bounds.X + row.Bounds.Width, row.Bounds.Y, 0, 0) : (DesignerRect?)null;
            if (anchor is { } at)
            {
                panels.Add(Panel(owner, at, header: null));
            }
        }

        _menuPanels = panels;
    }

    private DesignerMenuPanel Panel(DesignerComponentView owner, DesignerRect at, string? header)
    {
        var items = ItemsOf(owner.Name);
        var width = Math.Max(120, items.Select(i => TextWidth(i.Text) + 32).DefaultIfEmpty(0).Max());
        var rows = new List<DesignerMenuRow>();
        var y = at.Y + (header is null ? 0 : ItemHeight);
        foreach (var item in items)
        {
            var height = item.Type.EndsWith("Separator", StringComparison.Ordinal) ? SeparatorHeight : ItemHeight;
            rows.Add(new DesignerMenuRow(item.Name, item.Text ?? string.Empty, new DesignerRect(at.X, y, width, height), height == SeparatorHeight, item.HasDropDown));
            y += height;
        }

        var (property, types) = ItemsInfo(owner);
        var typeHere = new DesignerTypeHere(owner.Name, property, types, new DesignerRect(at.X, y, width, ItemHeight), CanType: true);
        return new DesignerMenuPanel(owner.Name, header, new DesignerRect(at.X, at.Y, width, y + ItemHeight - at.Y), rows, typeHere);
    }

    /// <summary>The menu row (or a panel's Type Here) at a point of the surface: (item, null) for an item, (null, Type Here) for a Type Here, or nothing.</summary>
    public (string? Item, DesignerTypeHere? TypeHere) MenuHitTest(double x, double y)
    {
        foreach (var panel in _menuPanels.Reverse())
        {
            if (panel.TypeHere.Bounds.Contains(x, y))
            {
                return (null, panel.TypeHere);
            }

            if (panel.Rows.FirstOrDefault(r => r.Bounds.Contains(x, y)) is { } row)
            {
                return (row.Item, null);
            }
        }

        return _stripTypeHere is { } strip && strip.Bounds.Contains(x, y) ? (null, strip) : (null, null);
    }

    /// <summary>
    /// Type Here: a new item at the end of a strip's or menu item's items: a menu item named from its text as the
    /// classic IDE names it (<c>&amp;Open</c> is <c>openToolStripMenuItem</c>), a separator for "-", or an item of the type picked.
    /// </summary>
    public async Task AddItemAsync(DesignerTypeHere typeHere, string? text, string? type = null)
    {
        // Only a strip's or a menu item's items take one: a stale box over a list (#67) would replace its items.
        if (_model is null || (StripOf(typeHere.Owner) is null && Find(typeHere.Owner) is not { IsItem: true, HasDropDown: true }))
        {
            return;
        }

        type ??= text == "-" ? "System.Windows.Forms.ToolStripSeparator" : typeHere.Types[0];
        var name = ItemName(type, text == "-" ? null : text);
        var items = ItemsOf(typeHere.Owner).Select(i => new DesignerCollectionItem { Name = i.Name }).ToList();
        items.Add(new DesignerCollectionItem { Name = name, Type = type, Text = text is null or "-" ? null : text });
        await SetCollectionAsync(typeHere.Owner, typeHere.Property, items).ConfigureAwait(true);
        if (Find(name) is not null)
        {
            SetSelection([name], notify: true);
        }
    }

    /// <summary>The classic IDE's name of a new item: its text's letters and digits, first one lowercased, then its type (<c>fileToolStripMenuItem</c>); without text, the type and a number.</summary>
    public string ItemName(string type, string? text)
    {
        var typeName = type[(type.LastIndexOf('.') + 1)..];
        var stem = new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        var taken = new HashSet<string>(_model?.Components.Select(c => c.Name) ?? [], StringComparer.Ordinal);
        taken.UnionWith(_components.Select(c => c.Name));
        if (stem.Length == 0 || char.IsDigit(stem[0]))
        {
            return NewName(type);
        }

        var name = char.ToLowerInvariant(stem[0]) + stem[1..] + typeName;
        for (var i = 1; taken.Contains(name); i++)
        {
            name = char.ToLowerInvariant(stem[0]) + stem[1..] + typeName + i.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    /// <summary>Moves an item up or down (left or right) among its owner's items.</summary>
    public Task MoveItemAsync(string item, int delta)
    {
        if (Find(item) is not { IsItem: true, Owner: { } owner } || Find(owner) is not { } ownerView)
        {
            return Task.CompletedTask;
        }

        var items = ItemsOf(owner).Select(i => i.Name).ToList();
        var index = items.IndexOf(item);
        var target = Math.Clamp(index + delta, 0, items.Count - 1);
        if (index < 0 || target == index)
        {
            return Task.CompletedTask;
        }

        items.RemoveAt(index);
        items.Insert(target, item);
        return SetCollectionAsync(owner, ItemsInfo(ownerView).Property, [.. items.Select(n => new DesignerCollectionItem { Name = n })]);
    }

    /// <summary>An item's text typed on the surface.</summary>
    public Task SetItemTextAsync(string item, string text) => SetPropertyAsync([item], "Text", text);
}

/// <summary>Format → Align's choices.</summary>
public enum DesignerAlignment
{
    Lefts,
    Centers,
    Rights,
    Tops,
    Middles,
    Bottoms,
    ToGrid,
}

/// <summary>Format → Make Same Size's choices.</summary>
public enum DesignerSizing
{
    Width,
    Height,
    Both,
    ToGrid,
}

/// <summary>Format → Horizontal and Vertical Spacing's choices.</summary>
public enum DesignerSpacing
{
    MakeEqual,
    Increase,
    Decrease,
    Remove,
}

/// <summary>A Type Here box: what it adds to (a strip's or menu item's items) and where it is.</summary>
public sealed record DesignerTypeHere(string Owner, string Property, string[] Types, DesignerRect Bounds, bool CanType);

/// <summary>A row of an open drop-down on the surface: an item, its text, where it is.</summary>
public sealed record DesignerMenuRow(string Item, string Text, DesignerRect Bounds, bool IsSeparator, bool HasDropDown);

/// <summary>An open drop-down (a menu item's, a context menu's) drawn by the surface: its rows and its Type Here.</summary>
public sealed record DesignerMenuPanel(string Owner, string? Header, DesignerRect Bounds, IReadOnlyList<DesignerMenuRow> Rows, DesignerTypeHere TypeHere);
