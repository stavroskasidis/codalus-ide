using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Codalus.Shell.Controls;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.Views;

/// <summary>An image file opened in the image viewer (M12b): PNG, JPEG, GIF (first frame), BMP, ICO and WebP.</summary>
public sealed class ImageDocument : ShellDocument
{
    /// <summary>The extensions the viewer opens. SVG opens as XML text. TODO(issue): render SVG and animate GIFs.</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp" };

    public ImageDocument(string path)
    {
        Path = path;
        Id = IdFor(path);
        Title = System.IO.Path.GetFileName(path);
    }

    public override string Kind => "image";

    public string Path { get; }

    public static string IdFor(string path) => "image:" + path;

    public static bool IsImage(string path) => Extensions.Contains(System.IO.Path.GetExtension(path));
}

/// <summary>Shows an image with zoom (Ctrl+wheel, the toolbar, Ctrl+= and Ctrl+-), fit-to-window and its pixel size.</summary>
public sealed class ImageViewerView : UserControl, IPaneView
{
    private static readonly double[] Steps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16];

    private readonly ImageDocument _document;
    private readonly Image _image = new() { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center };
    private Bitmap? _bitmap;
    private bool _fit = true;
    private double _zoom = 1;

    public ImageViewerView(ImageDocument document)
    {
        _document = document;
        Avalonia.Automation.AutomationProperties.SetName(_image, document.Title);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _image,
            Focusable = true,
        };
        _scroll.Bind(BackgroundProperty, _scroll.GetResourceObservable("Shell.Editor.Background"));
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _scroll.SizeChanged += (_, _) =>
        {
            if (_fit)
            {
                Apply();
            }
        };

        Button Tool(string text, string name, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 2), Classes = { "toolbar" } };
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            ToolTip.SetTip(button, name);
            button.Click += (_, _) => action();
            return button;
        }

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(6, 4),
            Children =
            {
                Tool("−", "Zoom out", () => Step(-1)),
                _zoomLabel,
                Tool("+", "Zoom in", () => Step(1)),
                Tool("1:1", "Actual size", () => SetZoom(1)),
                Tool("Fit", "Fit to window", () =>
                {
                    _fit = true;
                    Apply();
                }),
                _info,
            },
        };
        DockPanel.SetDock(toolbar, Avalonia.Controls.Dock.Top);
        Content = new DockPanel { Children = { toolbar, _scroll } };
        KeyDown += OnKeyDown;
        _ = LoadAsync();
    }

    /// <summary>The zoom factor shown (tests).</summary>
    public double Zoom => _zoom;

    /// <summary>The decoded image's size in pixels; null until it has loaded or when it could not be read.</summary>
    public PixelSize? PixelSize => _bitmap?.PixelSize;

    /// <summary>Completes when the image has been decoded (tests).</summary>
    public Task ImageLoaded { get; private set; } = Task.CompletedTask;

    public void FocusPrimary() => _scroll.Focus();

    private Task LoadAsync() => ImageLoaded = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        try
        {
            var path = _document.Path;
            _bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return new Bitmap(stream);
            }).ConfigureAwait(true);
            _image.Source = _bitmap;
            var bytes = new FileInfo(path).Length;
            _info.Text = string.Create(CultureInfo.CurrentCulture, $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height} px · {FormatSize(bytes)}");
            Apply();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            _info.Text = "This image could not be read: " + ex.Message;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };

    /// <summary>Zooms one step in (1) or out (-1).</summary>
    public void Step(int direction)
    {
        var next = direction > 0 ? Steps.FirstOrDefault(s => s > _zoom * 1.001, Steps[^1]) : Steps.LastOrDefault(s => s < _zoom * 0.999, Steps[0]);
        SetZoom(next);
    }

    public void SetZoom(double zoom)
    {
        _fit = false;
        _zoom = Math.Clamp(zoom, Steps[0], Steps[^1]);
        Apply();
    }

    private void Apply()
    {
        if (_bitmap is not { } bitmap)
        {
            return;
        }

        var size = bitmap.Size;
        if (_fit)
        {
            var available = _scroll.Bounds.Size;
            _zoom = available.Width <= 0 || available.Height <= 0
                ? 1
                : Math.Min(1, Math.Min((available.Width - 2) / size.Width, (available.Height - 2) / size.Height));
        }

        _image.Width = size.Width * _zoom;
        _image.Height = size.Height * _zoom;
        RenderOptions.SetBitmapInterpolationMode(_image, _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        _zoomLabel.Text = string.Create(CultureInfo.CurrentCulture, $"{_zoom * 100:0}%");
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 || (e.KeyModifiers & KeyModifiers.Meta) != 0)
        {
            Step(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                Step(1);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                Step(-1);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0:
                SetZoom(1);
                e.Handled = true;
                break;
        }
    }
}
