using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Codalus.Shell.Controls;

/// <summary>
/// A named icon from the shell's built-in set (ADR 0003: plugins refer to icons by name so they follow the
/// theme, including high contrast). Most names are Fluent UI System Icons (D24, M34a), compiled into geometries at
/// build time and filled in the current foreground; the rest are 16×16 stroke drawings. Command families color their
/// icons through classes (<c>accent-run</c>, <c>accent-git</c>…) whose brushes are theme tokens, monochrome in High
/// Contrast.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public Icon()
    {
        // High Contrast draws multicolor icons in the foreground, and "kind@Family" accents follow the theme.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> s_images = new(StringComparer.Ordinal);

    /// <summary>A plugin icon theme's raster icon (<c>img:&lt;path&gt;</c>), loaded once.</summary>
    private static Avalonia.Media.Imaging.Bitmap? Image(string path)
    {
        lock (s_images)
        {
            if (!s_images.TryGetValue(path, out var bitmap))
            {
                try
                {
                    bitmap = new Avalonia.Media.Imaging.Bitmap(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                {
                    bitmap = null;
                }

                s_images[path] = bitmap;
            }

            return bitmap;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Kind is { } image && image.StartsWith("img:", StringComparison.Ordinal))
        {
            if (Image(image[4..]) is { } bitmap)
            {
                var side = Math.Min(Bounds.Width, Bounds.Height);
                context.DrawImage(bitmap, new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side));
            }

            return;
        }

        if (Foreground is not { } brush)
        {
            return;
        }

        // "kind@Family" colors the icon with the family's token (Shell.Icon.Test…), for icons chosen in code.
        var kind = Kind;
        if (kind?.IndexOf('@', StringComparison.Ordinal) is int at and >= 0)
        {
            if (this.TryFindResource("Shell.Icon." + kind[(at + 1)..], ActualThemeVariant, out var accent) && accent is IBrush accentBrush)
            {
                brush = accentBrush;
            }

            kind = kind[..at];
        }
        else if (kind is not null && !kind.StartsWith(IconSet.MaterialPrefix, StringComparison.Ordinal) && !kind.StartsWith(IconSet.ClassicPrefix, StringComparison.Ordinal) && !IsSet(ForegroundProperty) && !Theming.ThemeService.IsHighContrast(ActualThemeVariant)
                 && CommandIcons.DefaultColorOf(kind) is { } color
                 && this.TryFindResource("Shell.Icon." + char.ToUpperInvariant(color[0]) + color[1..], ActualThemeVariant, out var token) && token is IBrush colored)
        {
            // Every icon is colored (the maintainer's rule): one nothing colors takes its kind's palette color.
            brush = colored;
        }

        var size = Math.Min(Bounds.Width, Bounds.Height);
        var offset = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);
        if (IconSet.GetFilled(kind) is { } filled)
        {
            // Multicolor icons (file types) take the Visual Studio palette's nearest color in the current theme (folders its
            // tan), except in High Contrast, where shape carries the meaning. The classic file icons' unfilled parts (pages,
            // outlines) are the theme's outline gray.
            var monochrome = Theming.ThemeService.IsHighContrast(ActualThemeVariant);
            var folder = kind!.StartsWith("mi:folder", StringComparison.Ordinal);
            var outline = kind.StartsWith(IconSet.ClassicPrefix, StringComparison.Ordinal)
                && this.TryFindResource("Shell.Icon.Outline", ActualThemeVariant, out var gray) && gray is IBrush outlineBrush ? outlineBrush : brush;
            var scale = size / filled.DesignSize;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            {
                foreach (var layer in filled.Layers)
                {
                    var fill = monochrome ? brush
                        : layer.Color == 0 ? outline
                        : (folder ? "Folder" : PaletteColor(layer.Color)) is { } family && this.TryFindResource("Shell.Icon." + family, ActualThemeVariant, out var token) && token is IBrush palette
                            ? palette
                            : filled.Brush(layer.Color);
                    context.DrawGeometry(fill, null, layer.Geometry);
                }
            }

            return;
        }

        if (IconSet.Get(kind) is not { } geometry)
        {
            return;
        }

        var strokeScale = size / 16;
        using (context.PushTransform(Matrix.CreateScale(strokeScale, strokeScale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(null, new Pen(brush, 1.25, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }

    private static readonly Dictionary<uint, string?> s_paletteColors = [];

    /// <summary>
    /// The palette color (<c>Shell.Icon.Blue</c>…) nearest a compiled icon's fill by hue, or null for a gray, which keeps
    /// its own color (a classic IDE's icons have gray outlines and pages).
    /// </summary>
    public static string? PaletteColor(uint argb)
    {
        lock (s_paletteColors)
        {
            if (s_paletteColors.TryGetValue(argb, out var known))
            {
                return known;
            }
        }

        var hsl = Color.FromUInt32(argb).ToHsl();
        string? family = hsl.S < 0.18 || hsl.L > 0.93 || hsl.L < 0.08 ? null
            : hsl.H is < 15 or >= 340 ? "Red"
            : hsl.H < 42 ? (hsl.L < 0.45 ? "Folder" : "Orange")
            : hsl.H < 70 ? "Yellow"
            : hsl.H < 165 ? "Green"
            : hsl.H < 255 ? "Blue"
            : "Purple";
        lock (s_paletteColors)
        {
            s_paletteColors[argb] = family;
        }

        return family;
    }
}

/// <summary>A compiled icon: its fills and the size it was drawn for (its view box: 16, 20, 24, 32…).</summary>
internal sealed class FilledIcon(IReadOnlyList<IconLayer> layers, double designSize)
{
    private readonly Dictionary<uint, IBrush> _brushes = [];

    public IReadOnlyList<IconLayer> Layers { get; } = layers;

    public double DesignSize { get; } = designSize;

    /// <summary>The bounds of all the fills (tests).</summary>
    public Rect Bounds => Layers.Select(l => l.Geometry.Bounds).Aggregate((a, b) => a.Union(b));

    public IBrush Brush(uint color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromUInt32(color));
            _brushes[color] = brush;
        }

        return brush;
    }
}

/// <summary>The built-in icons. Unknown names render nothing.</summary>
public static class IconSet
{
    /// <summary>Our icon names that are Fluent UI System Icons (D24), by the Fluent file name (Icons/fluent-icons.txt).</summary>
    private static readonly Dictionary<string, string> s_fluent = new(StringComparer.Ordinal)
    {
        // Names that existed before M34a, now drawn from Fluent.
        ["search"] = "search_16_regular",
        ["gear"] = "settings_16_regular",
        ["keyboard"] = "keyboard_16_regular",
        ["history"] = "history_16_regular",
        ["extensions"] = "puzzle_piece_16_regular",
        ["folder"] = "folder_16_regular",
        ["file"] = "document_16_regular",
        ["book"] = "book_16_regular",
        ["check"] = "checkmark_16_regular",
        ["home"] = "home_16_regular",
        ["info"] = "info_16_regular",
        ["warning"] = "warning_16_regular",
        ["error"] = "error_circle_16_regular",
        ["bell"] = "alert_16_regular",
        ["close"] = "dismiss_16_regular",
        ["refresh"] = "arrow_clockwise_16_regular",
        ["output"] = "window_text_16_regular",
        ["list"] = "list_16_regular",
        ["play"] = "play_16_filled",
        ["flame"] = "fire_16_regular",
        ["stop"] = "stop_16_filled",
        ["branch"] = "branch_16_regular",
        ["debug"] = "bug_16_regular",
        ["lock"] = "lock_closed_16_regular",
        ["arrow-down"] = "arrow_down_16_regular",
        ["lightbulb"] = "lightbulb_16_regular",
        ["star"] = "star_16_regular",
        ["edit"] = "edit_16_regular",
        ["trash"] = "delete_16_regular",
        ["pin"] = "pin_16_regular",
        ["beaker"] = "beaker_16_regular",
        ["filter"] = "filter_16_regular",
        ["coverage"] = "data_pie_16_regular",
        ["package"] = "box_16_regular",
        ["link"] = "link_16_regular",
        ["files"] = "document_multiple_16_regular",
        ["file-add"] = "document_add_16_regular",
        ["file-data"] = "document_data_16_regular",
        ["tools"] = "wrench_16_regular",
        ["arrow-left"] = "arrow_left_16_regular",
        ["arrow-right"] = "arrow_right_16_regular",
        ["save"] = "save_16_regular",
        ["save-all"] = "save_multiple_16_regular",
        ["undo"] = "arrow_undo_16_regular",
        ["redo"] = "arrow_redo_16_regular",
        ["play-outline"] = "play_16_regular",
        ["pause"] = "pause_16_regular",
        ["restart"] = "arrow_counterclockwise_16_regular",
        ["step-over"] = "arrow_step_over_16_regular",
        ["step-into"] = "arrow_step_in_16_regular",
        ["step-out"] = "arrow_step_out_16_regular",
        ["cloud"] = "cloud_16_regular",
        ["pull-request"] = "branch_request_16_regular",
        ["comment"] = "comment_16_regular",
        ["account"] = "person_16_regular",
        ["sync"] = "arrow_sync_16_regular",
        ["database"] = "database_16_regular",
        ["table"] = "table_16_regular",
        ["key"] = "key_16_regular",

        // Commands and tool windows in menus and toolbars (M34a).
        ["folder-open"] = "folder_open_16_regular",
        ["new"] = "new_16_regular",
        ["new-solution"] = "square_multiple_16_regular",
        ["compare"] = "branch_compare_16_regular",
        ["cut"] = "cut_16_regular",
        ["copy"] = "copy_16_regular",
        ["paste"] = "clipboard_paste_16_regular",
        ["find-in-files"] = "document_search_16_regular",
        ["replace"] = "arrow_swap_16_regular",
        ["go-to"] = "arrow_forward_16_regular",
        ["go-to-line"] = "text_number_list_ltr_16_regular",
        ["definition"] = "arrow_enter_16_regular",
        ["references"] = "link_multiple_16_regular",
        ["call-hierarchy"] = "flow_16_regular",
        ["type-hierarchy"] = "list_bar_tree_16_regular",
        ["rename"] = "rename_16_regular",
        ["format"] = "text_align_left_16_regular",
        ["uncomment"] = "comment_off_16_regular",
        ["duplicate"] = "square_multiple_16_regular",
        ["arrow-up"] = "arrow_up_16_regular",
        ["select-expand"] = "select_all_on_16_regular",
        ["fold"] = "text_collapse_16_regular",
        ["unfold"] = "text_expand_16_regular",
        ["fold-all"] = "arrow_collapse_all_16_regular",
        ["unfold-all"] = "arrow_expand_all_16_regular",
        ["bookmark"] = "bookmark_16_regular",
        ["bookmarks"] = "bookmark_multiple_16_regular",
        ["caret"] = "cursor_16_regular",
        ["snippet"] = "code_block_16_regular",
        ["braces"] = "braces_16_regular",
        ["broom"] = "broom_16_regular",
        ["code"] = "code_16_regular",
        ["terminal"] = "window_console_20_regular",
        ["task-list"] = "task_list_square_ltr_16_regular",
        ["window"] = "window_multiple_16_regular",
        ["tab"] = "tab_16_regular",
        ["download"] = "arrow_download_16_regular",
        ["upload"] = "arrow_upload_16_regular",
        ["fork"] = "branch_fork_16_regular",
        ["merge"] = "merge_16_regular",
        ["stash"] = "archive_16_regular",
        ["tag"] = "tag_16_regular",
        ["link-add"] = "link_add_16_regular",
        ["container"] = "cube_16_regular",
        ["publish"] = "cloud_arrow_up_16_regular",
        ["build"] = "toolbox_16_regular",
        ["toolbox"] = "toolbox_16_regular",
        ["cancel"] = "dismiss_circle_16_regular",
        ["layers"] = "stack_16_regular",
        ["timer"] = "timer_16_regular",
        ["check-circle"] = "checkmark_circle_16_regular",
        ["attach"] = "plug_connected_16_regular",
        ["breakpoint"] = "record_16_filled",
        ["breakpoint-outline"] = "record_16_regular",
        ["watch"] = "glasses_16_regular",
        ["gauge"] = "gauge_16_regular",
        ["globe"] = "globe_16_regular",
        ["shield"] = "shield_checkmark_16_regular",
        ["metrics"] = "data_bar_vertical_16_regular",
        ["color"] = "color_16_regular",
        ["templates"] = "document_copy_16_regular",
        ["split-right"] = "split_vertical_16_regular",
        ["split-down"] = "split_horizontal_16_regular",
        ["close-all"] = "dismiss_square_multiple_16_regular",
        ["layout"] = "layout_column_two_16_regular",
        ["document-text"] = "document_text_16_regular",
        ["update"] = "arrow_sync_circle_16_regular",
        ["sparkle"] = "sparkle_16_regular",
        ["phone"] = "phone_16_regular",

        // AI Chat (M34h, docs/design/controls/AiChat.dc.html): a new chat, sending a question, and a code block's Insert
        // at Cursor and Apply….
        ["chat-add"] = "chat_add_16_regular",
        ["send"] = "send_16_regular",
        ["insert"] = "arrow_import_16_regular",
        ["document-check"] = "document_checkmark_16_regular",

        // The control set's views (M34h): tool window toolbars and command rows as icon buttons; the WCF Test Client's
        // Add an item, the HTTP Response's time and Word Wrap.
        ["add"] = "add_16_regular",
        ["export"] = "arrow_export_ltr_16_regular",
        ["import"] = "arrow_import_16_regular",
        ["window-new"] = "window_new_16_regular",
        ["window-remote"] = "desktop_arrow_right_16_regular",
        ["cloud-download"] = "cloud_arrow_down_16_regular",
        ["radar"] = "radar_20_regular",
        ["reset"] = "arrow_reset_20_regular",
        ["arrow-sort"] = "arrow_sort_16_regular",
        ["unlock"] = "lock_open_16_regular",
        ["snapshot"] = "camera_16_regular",
        ["plug"] = "plug_connected_16_regular",
        ["detach"] = "plug_disconnected_16_regular",
        ["database-update"] = "database_arrow_up_16_regular",
        ["dump"] = "document_arrow_down_16_regular",
        ["cube"] = "cube_16_regular",
        ["mention"] = "number_symbol_16_regular",
        ["events"] = "flash_16_regular",
        ["breakpoint-solid"] = "circle_16_filled",
        ["breakpoint-hollow"] = "circle_16_regular",
        ["clock"] = "clock_16_regular",
        ["word-wrap"] = "text_wrap_16_regular",

        // The Windows Forms Toolbox's controls and components, one each (M34h; the stroke drawings below draw the
        // combo box, the progress bar and the group box, which Fluent has no picture of).
        ["control-pointer"] = "cursor_16_regular",
        ["control-button"] = "button_16_regular",
        ["control-check-box"] = "checkbox_checked_16_regular",
        ["control-checked-list-box"] = "multiselect_ltr_16_regular",
        ["control-date-time-picker"] = "calendar_clock_16_regular",
        ["control-label"] = "text_t_16_regular",
        ["control-link-label"] = "text_underline_16_regular",
        ["control-list-box"] = "text_bullet_list_ltr_16_regular",
        ["control-list-view"] = "content_view_gallery_16_regular",
        ["control-masked-text-box"] = "password_16_regular",
        ["control-month-calendar"] = "calendar_month_16_regular",
        ["control-notify-icon"] = "alert_badge_16_regular",
        ["control-numeric-up-down"] = "number_symbol_16_regular",
        ["control-picture-box"] = "image_16_regular",
        ["control-radio-button"] = "radio_button_16_filled",
        ["control-rich-text-box"] = "text_edit_style_16_regular",
        ["control-text-box"] = "textbox_16_regular",
        ["control-tool-tip"] = "tooltip_quote_16_regular",
        ["control-track-bar"] = "options_16_regular",
        ["control-tree-view"] = "text_bullet_list_tree_16_regular",
        ["control-web-browser"] = "globe_16_regular",
        ["control-flow-layout-panel"] = "text_wrap_16_regular",
        ["control-panel"] = "rectangle_landscape_16_regular",
        ["control-split-container"] = "split_vertical_16_regular",
        ["control-tab-control"] = "tab_desktop_16_regular",
        ["control-table-layout-panel"] = "layout_cell_four_16_regular",
        ["control-context-menu-strip"] = "text_bullet_list_square_16_regular",
        ["control-menu-strip"] = "navigation_16_regular",
        ["control-status-strip"] = "table_bottom_row_16_regular",
        ["control-tool-strip"] = "panel_top_gallery_16_regular",
        ["control-tool-strip-container"] = "layout_row_three_16_regular",
        ["control-binding-navigator"] = "arrow_next_16_regular",
        ["control-binding-source"] = "document_data_link_16_regular",
        ["control-data-grid-view"] = "table_16_regular",
        ["control-background-worker"] = "arrow_clockwise_dashes_16_regular",
        ["control-error-provider"] = "error_circle_16_regular",
        ["control-file-system-watcher"] = "eye_16_regular",
        ["eye-off"] = "eye_off_16_regular",
        ["control-help-provider"] = "question_circle_16_regular",
        ["control-image-list"] = "image_multiple_16_regular",
        ["control-process"] = "developer_board_16_regular",
        ["control-timer"] = "timer_16_regular",
        ["control-page-setup-dialog"] = "document_settings_16_regular",
        ["control-print-dialog"] = "print_16_regular",
        ["control-print-document"] = "document_one_page_16_regular",
        ["control-print-preview-control"] = "preview_link_16_regular",
        ["control-print-preview-dialog"] = "document_search_16_regular",
        ["control-color-dialog"] = "color_16_regular",
        ["control-folder-browser-dialog"] = "folder_list_16_regular",
        ["control-font-dialog"] = "text_font_16_regular",
        ["control-open-file-dialog"] = "folder_open_16_regular",
        ["control-save-file-dialog"] = "save_16_regular",
        ["control-generic"] = "window_16_regular",
        ["control-component"] = "settings_16_regular",
    };

    private static readonly Dictionary<string, FilledIcon?> s_filled = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, FilledIcon> s_vectors = new(StringComparer.Ordinal);

    /// <summary>The prefix of icon themes' converted SVG icons (<c>vec:&lt;path&gt;</c>, M22b).</summary>
    public const string VectorPrefix = "vec:";

    /// <summary>Whether an icon theme's SVG icon has been converted and registered.</summary>
    internal static bool HasVector(string svgPath)
    {
        lock (s_vectors)
        {
            return s_vectors.ContainsKey(VectorPrefix + svgPath);
        }
    }

    /// <summary>
    /// Registers an icon theme's SVG icon from its drawing operations (converted outside the shell, see
    /// <see cref="Codalus.Workbench.Plugins.IconThemeCompiler"/>), replayed the way the shell's own icons are.
    /// </summary>
    internal static void RegisterVector(string svgPath, string ops, float[] numbers, uint[] colors, double designSize)
    {
        var icon = new FilledIcon(CompiledIcons.Build(ops, numbers, colors), designSize);
        lock (s_vectors)
        {
            s_vectors[VectorPrefix + svgPath] = icon;
        }
    }

    /// <summary>Our Fluent icon names and the Fluent file each one uses (tests).</summary>
    public static IReadOnlyDictionary<string, string> FluentNames => s_fluent;

    /// <summary>The prefix of Material Icon Theme's file-type icons (<c>mi:csharp</c>, <c>mi:folder-src-open</c>; Controls/FileIcons.cs).</summary>
    public const string MaterialPrefix = "mi:";

    /// <summary>
    /// The prefix of the classic file, folder and project icons (<c>cl:csharp</c>, <c>cl:folder-open</c>; Icons/Classic,
    /// drawn by eng/icons/classic-icons.py; Controls/FileIcons.cs).
    /// </summary>
    public const string ClassicPrefix = "cl:";

    /// <summary>
    /// The code symbol icons of IntelliSense (the completion list, Quick Info, signature help): Icons/Symbols, drawn by
    /// eng/icons/symbol-icons.py, each colored by its kind (<see cref="CommandIcons"/>).
    /// </summary>
    private static readonly HashSet<string> s_symbols = new(StringComparer.Ordinal)
    {
        "symbol-class", "symbol-struct", "symbol-interface", "symbol-enum", "symbol-enum-member", "symbol-delegate",
        "symbol-method", "symbol-extension-method", "symbol-property", "symbol-field", "symbol-event", "symbol-constant",
        "symbol-local", "symbol-parameter", "symbol-namespace", "symbol-keyword", "symbol-snippet", "symbol-type-parameter",
        "symbol-operator", "symbol-module", "symbol-label", "symbol-text", "symbol-target-type",
    };

    /// <summary>The code symbol icons' names (tests).</summary>
    public static IReadOnlyCollection<string> SymbolNames => s_symbols;

    /// <summary>
    /// A compiled icon, or null when <paramref name="name"/> is a stroke icon or unknown: one of our Fluent names, or
    /// <c>mi:</c> and a Material Icon Theme file name, or <c>cl:</c> and a classic icon's name.
    /// </summary>
    internal static FilledIcon? GetFilled(string? name)
    {
        if (name is not null && name.StartsWith(VectorPrefix, StringComparison.Ordinal))
        {
            lock (s_vectors)
            {
                return s_vectors.GetValueOrDefault(name);
            }
        }

        string? file = null;
        if (name is not null && !s_fluent.TryGetValue(name, out file) && name.StartsWith(MaterialPrefix, StringComparison.Ordinal))
        {
            file = name[MaterialPrefix.Length..];
        }
        else if (name is not null && file is null && name.StartsWith(ClassicPrefix, StringComparison.Ordinal))
        {
            file = "classic-" + name[ClassicPrefix.Length..];
        }
        else if (name is not null && file is null && s_symbols.Contains(name))
        {
            file = name;
        }

        if (name is null || file is null)
        {
            return null;
        }

        lock (s_filled)
        {
            if (!s_filled.TryGetValue(name, out var icon))
            {
                icon = CompiledIcons.Create(file) is { } layers ? new FilledIcon(layers, CompiledIcons.DesignSize(file)) : null;
                s_filled[name] = icon;
            }

            return icon;
        }
    }
    private static readonly Dictionary<string, string> s_paths = new(StringComparer.Ordinal)
    {
        ["search"] = Circle(6.75, 6.75, 4.5) + "M10.25,10.25 L14,14",
        ["gear"] = Circle(8, 8, 2.25) + Circle(8, 8, 4.75) + Spokes(8, 8, 4.75, 6.75, 8),
        ["keyboard"] = "M1.5,4 H14.5 V12 H1.5 Z M4.5,9.5 H11.5 " + Dot(4, 6.75) + Dot(6.67, 6.75) + Dot(9.33, 6.75) + Dot(12, 6.75),
        ["color-mode"] = Circle(8, 8, 6) + "M8,2 V14",
        ["history"] = Circle(8, 8, 6) + "M8,4.5 V8 L10.5,9.5",
        ["extensions"] = "M2,2 H7 V7 H2 Z M9,2 H14 V7 H9 Z M2,9 H7 V14 H2 Z M9,9 H14 V14 H9 Z",
        ["folder"] = "M1.5,3.5 H6 L7.5,5 H14.5 V13 H1.5 Z",
        ["file"] = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5",
        ["book"] = "M8,4 C6.5,2.8 4.5,2.5 1.5,2.5 V13 C4.5,13 6.5,13.3 8,14.5 C9.5,13.3 11.5,13 14.5,13 V2.5 C11.5,2.5 9.5,2.8 8,4 Z M8,4 V14.5",
        ["check"] = "M3,8.5 L6.5,12 L13,4.5",
        ["circle"] = Circle(8, 8, 5),
        ["home"] = "M1.5,8 L8,2 L14.5,8 M3.5,6.5 V14 H12.5 V6.5 M6.5,14 V10 H9.5 V14",
        ["info"] = Circle(8, 8, 6.5) + "M8,7.25 V11.5 " + Dot(8, 4.75),
        ["warning"] = "M8,1.75 L14.75,13.75 H1.25 Z M8,6 V9.5 " + Dot(8, 11.75),
        ["error"] = Circle(8, 8, 6.5) + "M5.5,5.5 L10.5,10.5 M10.5,5.5 L5.5,10.5",
        ["bell"] = Bell,
        ["bell-dot"] = Bell + Circle(12.5, 3, 1.75),
        ["close"] = "M4,4 L12,12 M12,4 L4,12",
        ["chevron-right"] = "M6,3.5 L10.5,8 L6,12.5",
        ["chevron-down"] = "M3.5,6 L8,10.5 L12.5,6",
        ["chevron-up"] = "M3.5,10 L8,5.5 L12.5,10",
        ["zoom-in"] = Circle(6.75, 6.75, 4.5) + "M10.25,10.25 L14,14 M4.75,6.75 H8.75 M6.75,4.75 V8.75",
        ["zoom-out"] = Circle(6.75, 6.75, 4.5) + "M10.25,10.25 L14,14 M4.75,6.75 H8.75",
        ["fit"] = "M2,5.5 V2 H5.5 M10.5,2 H14 V5.5 M14,10.5 V14 H10.5 M5.5,14 H2 V10.5",
        ["chevron-double-right"] = "M3.5,3.5 L8,8 L3.5,12.5 M8.5,3.5 L13,8 L8.5,12.5",
        ["refresh"] = "M13,8 A5,5 0 1 1 11.54,4.46 M12,1.75 V4.75 H9",
        ["output"] = "M1.5,2.5 H14.5 V13.5 H1.5 Z M4,6 L6.5,8 L4,10 M8,10.5 H12",
        ["list"] = "M5.5,4 H14 M5.5,8 H14 M5.5,12 H14 " + Dot(2.5, 4) + Dot(2.5, 8) + Dot(2.5, 12),
        ["play"] = "M4.5,2.5 L13,8 L4.5,13.5 Z",
        ["flame"] = "M8,14.5 C4.75,14.5 3,12.25 3,9.75 C3,7 5.25,5.5 6,2 C8,3.5 8.5,5.5 8.25,7.25 C9.25,6.75 9.75,5.75 9.75,4.75 C11.75,6.25 13,8 13,10 C13,12.5 11,14.5 8,14.5 Z",
        ["stop"] = "M3.5,3.5 H12.5 V12.5 H3.5 Z",
        ["branch"] = Circle(4.5, 3.5, 1.75) + Circle(4.5, 12.5, 1.75) + Circle(11.5, 5, 1.75) + "M4.5,5.25 V10.75 M11.5,6.75 C11.5,9 4.5,8.5 4.5,10.75",
        ["debug"] = "M5.5,5.5 C5.5,4 6.6,3 8,3 C9.4,3 10.5,4 10.5,5.5 V10 C10.5,11.9 9.4,13.5 8,13.5 C6.6,13.5 5.5,11.9 5.5,10 Z M8,6.5 V13.5 M2.5,7.5 H5.5 M10.5,7.5 H13.5 M3,11.5 L5.5,10.5 M13,11.5 L10.5,10.5 M3,3.5 L5.5,5.5 M13,3.5 L10.5,5.5",
        ["link-external"] = "M9,2 H14 V7 M14,2 L7.5,8.5 M12,9.5 V14 H2 V4 H6.5",
        ["clear-all"] = "M2,4 H11 M2,8 H9 M2,12 H7 M10,9.5 L14,13.5 M14,9.5 L10,13.5",
        ["lock"] = "M4,7.5 H12 V14 H4 Z M5.75,7.5 V5 C5.75,3.5 6.75,2.5 8,2.5 C9.25,2.5 10.25,3.5 10.25,5 V7.5",
        ["arrow-down"] = "M8,2.5 V13 M4,9 L8,13 L12,9",
        ["lightbulb"] = "M5.75,10.5 C4.4,9.6 3.5,8.1 3.5,6.5 C3.5,4 5.5,2 8,2 C10.5,2 12.5,4 12.5,6.5 C12.5,8.1 11.6,9.6 10.25,10.5 V12 H5.75 Z M6.25,14 H9.75 ",
        ["star"] = "M8,1.75 L9.9,5.95 L14.4,6.35 L11,9.35 L12,13.85 L8,11.5 L4,13.85 L5,9.35 L1.6,6.35 L6.1,5.95 Z",
        ["question"] = Circle(8, 8, 6.5) + "M6,6.25 C6,5 7,4.25 8,4.25 C9.1,4.25 10,5 10,6 C10,7.25 8,7.5 8,9 " + Dot(8, 11.5),
        ["edit"] = "M10.5,2.5 L13.5,5.5 L5.5,13.5 H2.5 V10.5 Z M9,4 L12,7",
        ["discard"] = "M3,5.5 H10 C12,5.5 13.5,7 13.5,9 C13.5,11 12,12.5 10,12.5 H5 M5.5,3 L3,5.5 L5.5,8",
        ["trash"] = "M2.5,4 H13.5 M6,4 V2.5 H10 V4 M4,4 L4.75,14 H11.25 L12,4 M6.75,6.5 V11.5 M9.25,6.5 V11.5",
        ["layout"] = "M1.5,2.5 H14.5 V13.5 H1.5 Z M5.5,2.5 V13.5 M5.5,10 H14.5",
        ["dot"] = Circle(8, 8, 2),
        ["more"] = Dot(3, 8) + Dot(8, 8) + Dot(13, 8),
        ["arrow-up-down"] = "M5,13.5 V2.5 M2.5,5 L5,2.5 L7.5,5 M11,2.5 V13.5 M8.5,11 L11,13.5 L13.5,11",
        ["subtract"] = "M3.5,8 H12.5",
        ["pin"] = "M5.5,2 H10.5 M6.5,2 V6.5 L4,9.5 H12 L9.5,6.5 V2 M8,9.5 V14.5",

        // Toolbox controls Fluent has no picture of (M34h): a combo box (a box with its drop-down button), a progress
        // bar (a bar partly filled), a group box (a frame with its caption in the top edge).
        ["control-combo-box"] = "M1.5,4.5 H14.5 V11.5 H1.5 Z M10.5,4.5 V11.5 M11.5,7.25 L12.5,8.75 L13.5,7.25 M3.75,8 H8",
        ["control-progress-bar"] = "M1.5,5 H14.5 V11 H1.5 Z M3.5,7.5 H9.5 M3.5,8.5 H9.5",
        ["control-group-box"] = "M4.5,3.5 H2 V13.5 H14 V3.5 H11.5 M6,3.5 H10 M4.5,7 H11.5 M4.5,10 H9",

        // Test outcomes (Test Explorer, editor margin): colored by the test-* icon classes.
        ["test-passed"] = Circle(8, 8, 6) + "M5,8.25 L7.25,10.5 L11,6",
        ["test-failed"] = Circle(8, 8, 6) + "M5.75,5.75 L10.25,10.25 M10.25,5.75 L5.75,10.25",
        ["test-skipped"] = Circle(8, 8, 6) + "M5,8 H11",
        ["test-running"] = Circle(8, 8, 6) + "M8,4.5 V8 L10.5,9.5",
        ["test-none"] = Circle(8, 8, 6),
        ["beaker"] = "M6,1.5 H10 M6.75,1.5 V6 L2.75,13 C2.4,13.7 2.9,14.5 3.7,14.5 H12.3 C13.1,14.5 13.6,13.7 13.25,13 L9.25,6 V1.5 M4.5,10 H11.5",
        ["run-all"] = "M2.5,2.5 L8.5,6.5 L2.5,10.5 Z M7.5,5.5 L13.5,9.5 L7.5,13.5",
        ["filter"] = "M1.5,2.5 H14.5 L9.5,8.5 V13.5 L6.5,12 V8.5 Z",
        ["collapse-all"] = "M3.5,5.5 L8,10 L12.5,5.5 M2,2.5 H14",
        ["coverage"] = "M2.5,13.5 V2.5 M2.5,13.5 H13.5 M5,11 V8 M8,11 V5 M11,11 V9.5",
        ["namespace"] = "M5.5,2.5 C4.25,2.5 4,3.25 4,4.5 V6.5 C4,7.5 3.5,8 2.5,8 C3.5,8 4,8.5 4,9.5 V11.5 C4,12.75 4.25,13.5 5.5,13.5 M10.5,2.5 C11.75,2.5 12,3.25 12,4.5 V6.5 C12,7.5 12.5,8 13.5,8 C12.5,8 12,8.5 12,9.5 V11.5 C12,12.75 11.75,13.5 10.5,13.5",
        ["class"] = "M2.5,3 H13.5 V13 H2.5 Z M2.5,6.25 H13.5",
        ["method"] = "M8,2.25 L13,5.1 V10.9 L8,13.75 L3,10.9 V5.1 Z",
        ["solution"] = "M8,1.5 L14.5,8 L8,14.5 L1.5,8 Z M8,5 L11,8 L8,11 L5,8 Z",
        ["project"] = "M2.5,4.5 L8,1.75 L13.5,4.5 V11.5 L8,14.25 L2.5,11.5 Z M2.5,4.5 L8,7.25 L13.5,4.5 M8,7.25 V14.25",
        ["package"] = "M2.5,5 L8,2.25 L13.5,5 V11 L8,13.75 L2.5,11 Z M2.5,5 L8,7.75 L13.5,5 M8,7.75 V13.75 M5.25,3.6 L10.75,6.4",
        ["link"] = "M6.75,9.25 L9.25,6.75 M7.25,4.75 L8.75,3.25 C9.9,2.1 11.75,2.1 12.75,3.25 C13.9,4.25 13.9,6.1 12.75,7.25 L11.25,8.75 M8.75,11.25 L7.25,12.75 C6.1,13.9 4.25,13.9 3.25,12.75 C2.1,11.75 2.1,9.9 3.25,8.75 L4.75,7.25",
        ["file-code"] = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M6.75,8 L5.25,9.75 L6.75,11.5 M9.25,8 L10.75,9.75 L9.25,11.5",
        ["file-data"] = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M5.75,8 H10.25 M5.75,10 H10.25 M5.75,12 H8.75",

        // Project-system depth (M26): Show All Files, Add New Item, the Properties window, External Tools.
        ["files"] = "M5.5,1.5 H10.5 L13.5,4.5 V12 H5.5 Z M10.5,1.5 V4.5 H13.5 M3,4 V14.5 H11",
        ["file-add"] = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M8,7.5 V12.5 M5.5,10 H10.5",
        ["properties"] = "M2.5,2.5 H13.5 V13.5 H2.5 Z M2.5,6 H13.5 M2.5,9.75 H13.5 M6.5,2.5 V13.5",
        ["tools"] = "M13.5,4.5 L11,7 L9,5 L11.5,2.5 C9.75,1.75 7.5,2.75 7.25,4.75 C7.15,5.4 7.3,6 7.5,6.5 L2.25,11.75 C1.7,12.3 1.7,13.2 2.25,13.75 C2.8,14.3 3.7,14.3 4.25,13.75 L9.5,8.5 C10,8.7 10.6,8.85 11.25,8.75 C13.25,8.5 14.25,6.25 13.5,4.5 Z",

        // The standard toolbar (M29).
        ["arrow-left"] = "M13,8 H3 M7,4 L3,8 L7,12",
        ["arrow-right"] = "M3,8 H13 M9,4 L13,8 L9,12",
        ["save"] = "M2.5,2.5 H11.5 L13.5,4.5 V13.5 H2.5 Z M5,2.5 V6 H10.5 V2.5 M5,13.5 V9.5 H11 V13.5",
        ["save-all"] = "M4.5,1.5 H12 L14.5,4 V11.5 M2.5,4.5 H10 L11.5,6 V14.5 H2.5 Z M4.5,4.5 V7.5 H9 V4.5 M4.5,14.5 V11 H9.5 V14.5",
        ["undo"] = "M4.5,6.5 H10 C12,6.5 13.5,8 13.5,10 C13.5,12 12,13.5 10,13.5 H6 M7,4 L4.5,6.5 L7,9",
        ["redo"] = "M11.5,6.5 H6 C4,6.5 2.5,8 2.5,10 C2.5,12 4,13.5 6,13.5 H10 M9,4 L11.5,6.5 L9,9",
        ["play-outline"] = "M4.5,2.5 L13,8 L4.5,13.5 Z M7,6.25 L9.75,8 L7,9.75",
        ["pause"] = "M5,3 V13 M11,3 V13",
        ["restart"] = "M13,8 A5,5 0 1 1 11.54,4.46 M12,1.75 V4.75 H9 M7,6 L10,8 L7,10 Z",
        ["step-over"] = "M2.5,9 C2.5,5 5,3 8,3 C11,3 13.5,5 13.5,8.5 M11,6.5 L13.5,8.75 L15.5,6.25 M8,12.5 m-1.5,0 a1.5,1.5 0 1,0 3,0 a1.5,1.5 0 1,0 -3,0",
        ["step-into"] = "M8,1.5 V9 M5,6 L8,9 L11,6 M8,13 m-1.5,0 a1.5,1.5 0 1,0 3,0 a1.5,1.5 0 1,0 -3,0",
        ["step-out"] = "M8,9.5 V2 M5,5 L8,2 L11,5 M8,13 m-1.5,0 a1.5,1.5 0 1,0 3,0 a1.5,1.5 0 1,0 -3,0",

        // Git hosting and repositories (M13b).
        ["cloud"] = "M4.5,12.5 C2.8,12.5 1.5,11.2 1.5,9.6 C1.5,8.1 2.6,6.9 4.1,6.75 C4.6,4.6 6.2,3.25 8.25,3.25 C10.4,3.25 12.1,4.9 12.35,7 C13.6,7.25 14.5,8.3 14.5,9.65 C14.5,11.2 13.25,12.5 11.6,12.5 Z",
        ["pull-request"] = Circle(4, 3.5, 1.5) + Circle(4, 12.5, 1.5) + Circle(12, 12.5, 1.5) + "M4,5 V11 M12,11 V6 C12,4.75 11.25,4 10,4 H7 M8.5,2.5 L7,4 L8.5,5.5",
        ["comment"] = "M2,3 H14 V11 H8 L5,13.75 V11 H2 Z",
        ["issue"] = Circle(8, 8, 6.25) + Circle(8, 8, 1.5),
        ["worktree"] = "M8,14.5 V8 M8,8 C8,6 5.5,6 4,4.5 M8,8 C8,6 10.5,6 12,4.5 " + Circle(4, 3.5, 1.5) + Circle(12, 3.5, 1.5) + Circle(8, 14, 0.75),
        ["account"] = Circle(8, 5.5, 3) + "M2.5,14.5 C2.5,11.5 5,9.5 8,9.5 C11,9.5 13.5,11.5 13.5,14.5",
        ["sync"] = "M13,8 A5,5 0 0 1 3.76,10.65 M3,8 A5,5 0 0 1 12.24,5.35 M12.5,2.5 V5.5 H9.5 M3.5,13.5 V10.5 H6.5",

        // Data and Entity Framework (M27).
        ["database"] = "M2.5,3.75 C2.5,2.5 5,1.75 8,1.75 C11,1.75 13.5,2.5 13.5,3.75 C13.5,5 11,5.75 8,5.75 C5,5.75 2.5,5 2.5,3.75 Z M2.5,3.75 V12.25 C2.5,13.5 5,14.25 8,14.25 C11,14.25 13.5,13.5 13.5,12.25 V3.75 M2.5,8 C2.5,9.25 5,10 8,10 C11,10 13.5,9.25 13.5,8",
        ["table"] = "M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,6 H14.5 M1.5,9.75 H14.5 M6,6 V13.5 M10.5,6 V13.5",
        ["view"] = "M1.5,2.5 H14.5 V13.5 H1.5 Z M1.5,6 H14.5 M4,9.75 C5.5,8 10.5,8 12,9.75 C10.5,11.5 5.5,11.5 4,9.75 Z",
        ["column"] = "M5.5,1.5 H10.5 V14.5 H5.5 Z M5.5,5 H10.5",
        ["key"] = Circle(5, 8, 3) + "M8,8 H14.5 M12.5,8 V10.5 M10.5,8 V10",
        ["function"] = "M10.5,2.5 C8.5,2.5 8,3.5 7.75,5 L6.75,11 C6.5,12.5 6,13.5 4,13.5 M5,6.5 H10.5",
        ["procedure"] = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M9.5,1.5 V4.5 H12.5 M6,7.5 L8,9.5 L6,11.5 M8.5,11.5 H10.5",
        ["schema"] = "M1.5,3.5 H6.5 L8,5 H14.5 V13.5 H1.5 Z M4,8.5 H12 M4,11 H10",
        ["migration"] = "M2.5,4.5 H10.5 M8,2 L10.5,4.5 L8,7 M13.5,11.5 H5.5 M8,9 L5.5,11.5 L8,14",
        ["diagram"] = "M1.5,1.5 H6.5 V5.5 H1.5 Z M9.5,10.5 H14.5 V14.5 H9.5 Z M9.5,1.5 H14.5 V5.5 H9.5 Z M4,5.5 V12.5 H9.5 M6.5,3.5 H9.5",
        ["plan"] = "M2.5,2 H7.5 V5 H2.5 Z M8.5,11 H13.5 V14 H8.5 Z M2.5,11 H7.5 V14 H2.5 Z M5,5 V11 M5,8 H11 V11",
    };

    private static readonly Dictionary<string, Geometry> s_cache = new(StringComparer.Ordinal);

    private static string Bell => "M3.5,11.5 V7 C3.5,4.5 5.5,2.5 8,2.5 C10.5,2.5 12.5,4.5 12.5,7 V11.5 L13.5,12.5 H2.5 Z M6.5,14.25 H9.5 ";

    /// <summary>Every icon name, Fluent and stroke.</summary>
    public static IReadOnlyCollection<string> Names => [.. s_paths.Keys.Union(s_fluent.Keys, StringComparer.Ordinal).Union(s_symbols, StringComparer.Ordinal)];

    public static Geometry? Get(string? name)
    {
        if (name is null || !s_paths.TryGetValue(name, out var path))
        {
            return null;
        }

        if (!s_cache.TryGetValue(name, out var geometry))
        {
            geometry = StreamGeometry.Parse(path);
            s_cache[name] = geometry;
        }

        return geometry;
    }

    private static string Circle(double cx, double cy, double r) =>
        string.Create(CultureInfo.InvariantCulture, $"M{cx - r},{cy} A{r},{r} 0 1 1 {cx + r},{cy} A{r},{r} 0 1 1 {cx - r},{cy} Z ");

    private static string Dot(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"M{x},{y} L{x + 0.01},{y} ");

    private static string Spokes(double cx, double cy, double inner, double outer, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var angle = i * 2 * Math.PI / count;
            var (sin, cos) = Math.SinCos(angle);
            sb.Append(CultureInfo.InvariantCulture, $"M{cx + (inner * cos):0.###},{cy + (inner * sin):0.###} L{cx + (outer * cos):0.###},{cy + (outer * sin):0.###} ");
        }

        return sb.ToString();
    }
}
