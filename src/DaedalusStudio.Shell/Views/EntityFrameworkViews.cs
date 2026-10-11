using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The model view (M27a, ADR 0036): a read-only diagram of a context's model, and its debug view.</summary>
public sealed class EfModelView : UserControl, IPaneView
{
    private readonly EfModelViewModel _vm;
    private readonly EfDiagram _diagram = new();
    private readonly CodeViewer _debugView;
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly TextBlock _message = new() { Margin = new Thickness(12, 8), TextWrapping = TextWrapping.Wrap };
    private readonly TabControl _tabs;

    public EfModelView(EfModelViewModel vm)
    {
        _vm = vm;
        _debugView = new CodeViewer(vm.Services, vm.Font);
        AutomationProperties.SetName(_debugView.Editor, "Model debug view (read-only)");
        var refresh = new Button { Classes = { "toolbar" }, Command = vm.RefreshCommand, Content = new Icon { Kind = "refresh" } };
        ToolTip.SetTip(refresh, "Build and read the model again");
        AutomationProperties.SetName(refresh, "Refresh the model");
        var header = new DockPanel { Margin = new Thickness(4, 3) };
        DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Left);
        header.Children.Add(refresh);
        header.Children.Add(_summary);

        _tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Diagram", Content = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _diagram } },
                new TabItem { Header = "Debug View", Content = _debugView },
            },
        };
        var root = new DockPanel();
        DockPanel.SetDock(header, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(_message, Avalonia.Controls.Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_message);
        root.Children.Add(_tabs);
        Content = root;
        vm.ModelChanged += (_, _) => Update();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EfModelViewModel.Message))
            {
                Update();
            }
        };
        Update();
    }

    /// <summary>The diagram (tests and screenshots).</summary>
    public EfDiagram Diagram => _diagram;

    private void Update()
    {
        _summary.Text = _vm.Summary;
        _message.Text = _vm.Message;
        _message.IsVisible = _vm.HasMessage;
        _diagram.Model = _vm.Model;
        _debugView.Show(_vm.Model?.DebugView ?? string.Empty, null, null);
    }

    public void FocusPrimary() => _tabs.Focus();
}

/// <summary>
/// The entity diagram: a box per entity type (its table, keys and properties), owned types next to their owner, and a
/// line per foreign key from the dependent to the principal with its multiplicity and delete behaviour (dashed for
/// ownership). Laid out in a grid, principals first. Drawn with the theme's brushes.
/// </summary>
public sealed class EfDiagram : Control
{
    public static readonly StyledProperty<EfModelResult?> ModelProperty = AvaloniaProperty.Register<EfDiagram, EfModelResult?>(nameof(Model));

    private const double BoxWidth = 236;
    private const double HeaderHeight = 38;
    private const double RowHeight = 17;
    private const double Gap = 56;
    private const int MaxRows = 14;

    private readonly Dictionary<string, Rect> _boxes = new(StringComparer.Ordinal);

    static EfDiagram()
    {
        AffectsMeasure<EfDiagram>(ModelProperty);
        AffectsRender<EfDiagram>(ModelProperty);
    }

    public EfDiagram()
    {
        AutomationProperties.SetName(this, "Entity diagram");
    }

    public EfModelResult? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>Where each entity type was laid out (tests).</summary>
    public IReadOnlyDictionary<string, Rect> Boxes => _boxes;

    private static string Short(string? name) => name is null ? string.Empty : name[(name.LastIndexOf('.') + 1)..];

    private static int Rows(EfEntityInfo entity) => Math.Min(entity.Properties.Length, MaxRows) + (entity.Properties.Length > MaxRows ? 1 : 0);

    private static double BoxHeight(EfEntityInfo entity) => HeaderHeight + (Rows(entity) * RowHeight) + 8;

    /// <summary>Principals before dependents, owned types right after their owner.</summary>
    private static List<EfEntityInfo> Order(EfModelResult model)
    {
        var byName = model.Entities.ToDictionary(e => e.Name, StringComparer.Ordinal);
        int Rank(EfEntityInfo entity, int depth) => depth > 8 ? 0 : entity.ForeignKeys
            .Where(f => !f.IsOwnership && f.Principal != entity.Name && byName.ContainsKey(f.Principal))
            .Select(f => Rank(byName[f.Principal], depth + 1) + 1)
            .DefaultIfEmpty(0)
            .Max();

        var ordered = new List<EfEntityInfo>();
        void Add(EfEntityInfo entity)
        {
            ordered.Add(entity);
            foreach (var owned in model.Entities.Where(e => e.IsOwned && e.Owner == entity.Name && !ordered.Contains(e)))
            {
                Add(owned);
            }
        }

        foreach (var entity in model.Entities.Where(e => !e.IsOwned).OrderBy(e => Rank(e, 0)).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            Add(entity);
        }

        ordered.AddRange(model.Entities.Where(e => !ordered.Contains(e)));
        return ordered;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _boxes.Clear();
        if (Model is not { Entities.Length: > 0 } model)
        {
            return new Size(0, 0);
        }

        var ordered = Order(model);
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(ordered.Count)));
        double y = 16, width = 0;
        for (var start = 0; start < ordered.Count; start += columns)
        {
            var row = ordered.Skip(start).Take(columns).ToList();
            double x = 16, rowHeight = 0;
            foreach (var entity in row)
            {
                var height = BoxHeight(entity);
                _boxes[entity.Name] = new Rect(x, y, BoxWidth, height);
                x += BoxWidth + Gap;
                rowHeight = Math.Max(rowHeight, height);
            }

            width = Math.Max(width, x);
            y += rowHeight + Gap;
        }

        return new Size(width, y);
    }

    public override void Render(DrawingContext context)
    {
        if (Model is not { } model)
        {
            return;
        }

        var foreground = Brush("Shell.Foreground", Brushes.Black);
        var muted = Brush("Shell.MutedForeground", Brushes.Gray);
        var border = Brush("Shell.Border", Brushes.Gray);
        var panel = Brush("Shell.Panel", Brushes.White);
        var header = Brush("Shell.Caption.Active", Brushes.SteelBlue);
        var headerText = Brush("Shell.Caption.Active.Foreground", Brushes.White);
        var accent = Brush("Shell.Accent", Brushes.SteelBlue);
        var linePen = new Pen(muted, 1.2);
        var ownedPen = new Pen(muted, 1.2, new DashStyle([4, 3], 0));
        var typeface = new Typeface(FontFamily.Default);
        var bold = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        // Relationships first, under the boxes.
        foreach (var entity in model.Entities)
        {
            if (!_boxes.TryGetValue(entity.Name, out var dependent))
            {
                continue;
            }

            foreach (var fk in entity.ForeignKeys)
            {
                if (!_boxes.TryGetValue(fk.Principal, out var principal) || fk.Principal == entity.Name)
                {
                    continue;
                }

                var (from, to) = Connect(dependent, principal);
                context.DrawLine(fk.IsOwnership ? ownedPen : linePen, from, to);
                DrawArrow(context, fk.IsOwnership ? ownedPen : linePen, from, to);
                var many = fk.IsUnique ? (fk.IsRequired ? "1" : "0..1") : "*";
                var one = fk.IsRequired ? "1" : "0..1";
                Text(context, fk.IsOwnership ? "owned" : many, Lerp(from, to, 0.12) + new Vector(4, -14), muted, typeface, 10);
                Text(context, one, Lerp(from, to, 0.88) + new Vector(4, -14), muted, typeface, 10);
                if (!fk.IsOwnership && fk.DeleteBehavior is { } behavior && behavior != "ClientSetNull")
                {
                    Text(context, "on delete: " + behavior, Lerp(from, to, 0.5) + new Vector(4, 2), muted, typeface, 10);
                }
            }
        }

        foreach (var entity in model.Entities)
        {
            if (!_boxes.TryGetValue(entity.Name, out var box))
            {
                continue;
            }

            context.DrawRectangle(panel, new Pen(entity.IsOwned ? muted : border, 1, entity.IsOwned ? new DashStyle([4, 3], 0) : null), box, 3, 3);
            var head = new Rect(box.X, box.Y, box.Width, HeaderHeight);
            context.DrawRectangle(entity.IsOwned ? muted : header, null, head, 3, 3);
            Text(context, Short(entity.Name) + (entity.IsOwned ? " (owned)" : entity.IsKeyless ? " (keyless)" : string.Empty), new Point(box.X + 8, box.Y + 4), headerText, bold, 12.5, box.Width - 16);
            var table = entity.View is { } view ? "view " + view : entity.Table is { } t ? (entity.Schema is { } s ? s + "." + t : t) : entity.Owner is { } owner ? "in " + Short(owner) : string.Empty;
            Text(context, table, new Point(box.X + 8, box.Y + 21), headerText, typeface, 10.5, box.Width - 16);

            var y = box.Y + HeaderHeight + 4;
            foreach (var property in entity.Properties.Take(MaxRows))
            {
                var marker = property.IsKey ? "PK" : property.IsForeignKey ? "FK" : string.Empty;
                if (marker.Length > 0)
                {
                    Text(context, marker, new Point(box.X + 6, y), accent, bold, 9.5);
                }

                Text(context, property.Name, new Point(box.X + 28, y), foreground, property.IsKey ? bold : typeface, 11.5, 120);
                var type = (property.ClrType ?? string.Empty) + (property.MaxLength is { } max ? $"({max.ToString(CultureInfo.InvariantCulture)})" : string.Empty);
                Text(context, type, new Point(box.X + 150, y), muted, typeface, 10.5, box.Width - 156);
                y += RowHeight;
            }

            if (entity.Properties.Length > MaxRows)
            {
                Text(context, $"… {entity.Properties.Length - MaxRows} more", new Point(box.X + 28, y), muted, typeface, 10.5);
            }
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static void Text(DrawingContext context, string text, Point at, IBrush brush, Typeface typeface, double size, double maxWidth = 0)
    {
        if (text.Length == 0)
        {
            return;
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
        if (maxWidth > 0)
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }

        context.DrawText(formatted, at);
    }

    private static Point Lerp(Point a, Point b, double t) => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    /// <summary>The points where the line between two boxes' centers leaves each box.</summary>
    private static (Point From, Point To) Connect(Rect a, Rect b)
    {
        var ca = a.Center;
        var cb = b.Center;
        return (Edge(a, ca, cb), Edge(b, cb, ca));
    }

    private static Point Edge(Rect box, Point center, Point toward)
    {
        var dx = toward.X - center.X;
        var dy = toward.Y - center.Y;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
        {
            return center;
        }

        var sx = Math.Abs(dx) < 0.001 ? double.MaxValue : (box.Width / 2) / Math.Abs(dx);
        var sy = Math.Abs(dy) < 0.001 ? double.MaxValue : (box.Height / 2) / Math.Abs(dy);
        var s = Math.Min(sx, sy);
        return new Point(center.X + (dx * s), center.Y + (dy * s));
    }

    private static void DrawArrow(DrawingContext context, Pen pen, Point from, Point to)
    {
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double length = 9;
        var left = new Point(to.X - (length * Math.Cos(angle - 0.4)), to.Y - (length * Math.Sin(angle - 0.4)));
        var right = new Point(to.X - (length * Math.Cos(angle + 0.4)), to.Y - (length * Math.Sin(angle + 0.4)));
        context.DrawLine(pen, to, left);
        context.DrawLine(pen, to, right);
    }
}
