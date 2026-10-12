using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Previewer;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>
/// The XAML Previewer tool window (M19a, ADR 0048): the designer host's frames, zoom, the render scale and the theme
/// variant, the loader's error, and the selected element's adorner. The shell (<c>ShellHost.Xaml.cs</c>) drives it.
/// </summary>
public sealed class XamlPreviewViewModel : ObservableObject
{
    public const string ToolWindowId = "xaml.preview";

    private static readonly double[] s_zooms = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    private string _status = "Open an .axaml file to preview it.";
    private string? _error;
    private string? _fileName;
    private WriteableBitmap? _image;
    private WriteableBitmap? _previous;
    private PreviewFrame? _frame;
    private double _zoom = 1;
    private string _scale = "100%";
    private string _theme = "Default";
    private PreviewRect? _selection;
    private int _frames;

    public XamlPreviewViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = Invoke(RefreshRequested));
        ZoomInCommand = new RelayCommand(() => Zoom = s_zooms.FirstOrDefault(z => z > _zoom + 0.001, s_zooms[^1]));
        ZoomOutCommand = new RelayCommand(() => Zoom = s_zooms.LastOrDefault(z => z < _zoom - 0.001, s_zooms[0]));
        ZoomResetCommand = new RelayCommand(() => Zoom = 1);
        FitCommand = new RelayCommand(() =>
        {
            FitToPane = true;
            FitRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Refresh: rebuild if needed and render again.</summary>
    public Func<Task>? RefreshRequested { get; set; }

    /// <summary>The render scale changed (1 = 96 DPI).</summary>
    public Func<double, Task>? ScaleChanged { get; set; }

    /// <summary>The theme variant changed (<c>Default</c>, <c>Light</c>, <c>Dark</c>).</summary>
    public Func<string, Task>? ThemeChanged { get; set; }

    /// <summary>A click at a point of the preview (its device-independent pixels).</summary>
    public Func<double, double, Task>? Clicked { get; set; }

    /// <summary>The pointer moved over the preview (device-independent pixels).</summary>
    public Func<double, double, Task>? PointerMoved { get; set; }

    public ICommand RefreshCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand ZoomResetCommand { get; }

    /// <summary>Fit: the preview scales down to the pane (and follows it) until a zoom command sets a zoom.</summary>
    public ICommand FitCommand { get; }

    /// <summary>Whether the zoom follows the pane's size (the default): a frame larger than the pane is shown whole.</summary>
    public bool FitToPane { get; private set; } = true;

    /// <summary>The view should fit the frame to its pane now.</summary>
    public event EventHandler? FitRequested;

    /// <summary>Fits the frame into <paramref name="width"/> × <paramref name="height"/> (device-independent pixels), never above 100%.</summary>
    public void FitTo(double width, double height)
    {
        if (!FitToPane || _frame is null || width <= 0 || height <= 0)
        {
            return;
        }

        var zoom = Math.Min(1, Math.Min(width / (_frame.Width / _frame.Scale), height / (_frame.Height / _frame.Scale)));
        SetZoom(Math.Max(0.1, zoom), fit: true);
    }

    public IReadOnlyList<string> Scales { get; } = ["100%", "125%", "150%", "200%"];

    public IReadOnlyList<string> Themes { get; } = ["Default", "Light", "Dark"];

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    /// <summary>The XAML loader's error (with its line), or null.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The file being previewed.</summary>
    public string? FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    public WriteableBitmap? Image
    {
        get => _image;
        private set => SetField(ref _image, value);
    }

    public bool HasImage => _image is not null;

    /// <summary>The frames shown so far (tests and the smoke test wait on it).</summary>
    public int FrameCount => _frames;

    /// <summary>The newest frame.</summary>
    public PreviewFrame? Frame => _frame;

    /// <summary>The preview's size in the tool window: the frame's device-independent size times the zoom.</summary>
    public double ImageWidth => _frame is null ? 0 : _frame.Width / _frame.Scale * _zoom;

    public double ImageHeight => _frame is null ? 0 : _frame.Height / _frame.Scale * _zoom;

    public double Zoom
    {
        get => _zoom;
        set => SetZoom(Math.Clamp(value, s_zooms[0], s_zooms[^1]), fit: false);
    }

    private void SetZoom(double zoom, bool fit)
    {
        FitToPane = fit;
        if (SetField(ref _zoom, zoom, nameof(Zoom)))
        {
            OnPropertyChanged(nameof(ZoomText));
            OnSizeChanged();
        }
    }

    public string ZoomText => (_zoom * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public string SelectedScale
    {
        get => _scale;
        set
        {
            if (SetField(ref _scale, value) && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            {
                _ = Invoke(ScaleChanged, percent / 100);
            }
        }
    }

    public string SelectedTheme
    {
        get => _theme;
        set
        {
            if (SetField(ref _theme, value))
            {
                _ = Invoke(ThemeChanged, value);
            }
        }
    }

    /// <summary>The selected element's bounds in the previewed window (device-independent pixels), or null.</summary>
    public PreviewRect? Selection
    {
        get => _selection;
        set
        {
            if (SetField(ref _selection, value))
            {
                OnSizeChanged();
            }
        }
    }

    public bool HasSelection => _selection is { Width: > 0, Height: > 0 };

    public double SelectionLeft => (_selection?.X ?? 0) * _zoom;

    public double SelectionTop => (_selection?.Y ?? 0) * _zoom;

    public double SelectionWidth => (_selection?.Width ?? 0) * _zoom;

    public double SelectionHeight => (_selection?.Height ?? 0) * _zoom;

    private void OnSizeChanged()
    {
        OnPropertyChanged(nameof(ImageWidth));
        OnPropertyChanged(nameof(ImageHeight));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLeft));
        OnPropertyChanged(nameof(SelectionTop));
        OnPropertyChanged(nameof(SelectionWidth));
        OnPropertyChanged(nameof(SelectionHeight));
    }

    /// <summary>Shows a frame (UI thread): a new bitmap each time, so the image redraws; the one before is released a frame later.</summary>
    public void ShowFrame(PreviewFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Data.Length < frame.Stride * frame.Height)
        {
            return;
        }

        var format = frame.Format switch
        {
            PreviewPixelFormat.Bgra8888 => PixelFormat.Bgra8888,
            PreviewPixelFormat.Rgb565 => PixelFormat.Rgb565,
            _ => PixelFormat.Rgba8888,
        };
        var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96 * frame.Scale, 96 * frame.Scale), format, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            var rowBytes = Math.Min(buffer.RowBytes, frame.Stride);
            for (var y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(frame.Data, y * frame.Stride, buffer.Address + y * buffer.RowBytes, rowBytes);
            }
        }

        _previous?.Dispose();
        _previous = _image;
        _frame = frame;
        _frames++;
        Image = bitmap;
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(FrameCount));
        OnSizeChanged();
    }

    /// <summary>Clears the preview (another file, or none).</summary>
    public void Clear(string status)
    {
        _previous?.Dispose();
        _previous = _image;
        _frame = null;
        Image = null;
        Selection = null;
        Error = null;
        Status = status;
        OnPropertyChanged(nameof(HasImage));
        OnSizeChanged();
    }

    public Task ClickAsync(double x, double y) => Invoke(Clicked, x, y);

    public Task MoveAsync(double x, double y) => Invoke(PointerMoved, x, y);

    private static Task Invoke(Func<Task>? handler) => handler?.Invoke() ?? Task.CompletedTask;

    private static Task Invoke<T>(Func<T, Task>? handler, T value) => handler?.Invoke(value) ?? Task.CompletedTask;

    private static Task Invoke(Func<double, double, Task>? handler, double x, double y) => handler?.Invoke(x, y) ?? Task.CompletedTask;
}
