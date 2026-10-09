using Avalonia.Media;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Infrastructure;

/// <summary>The font of text views (Output, text documents), from <c>editor.fontFamily</c> and <c>editor.fontSize</c>.</summary>
public sealed class EditorFont : ObservableObject
{
    private FontFamily _family = FontFamily.Default;
    private double _size = 13;

    public EditorFont(SettingsService settings)
    {
        Update(settings);
        settings.Changed += (_, e) =>
        {
            if (e.Affects("editor.fontFamily") || e.Affects("editor.fontSize"))
            {
                Ui.Post(() => Update(settings));
            }
        };
    }

    public FontFamily Family
    {
        get => _family;
        private set => SetField(ref _family, value);
    }

    public double Size
    {
        get => _size;
        private set => SetField(ref _size, value);
    }

    private void Update(SettingsService settings)
    {
        var family = settings.GetString("editor.fontFamily");
        Family = string.IsNullOrWhiteSpace(family) ? FontFamily.Default : new FontFamily(family);
        Size = Math.Clamp(settings.GetDouble("editor.fontSize", 13), 6, 72);
    }
}
