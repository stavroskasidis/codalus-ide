using Avalonia.Media;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;

namespace Codalus.Shell;

/// <summary>
/// Fonts and Colors (M23a): the user's colors over the active theme, from <c>editor.tokenColorCustomizations</c> and
/// <c>workbench.colorCustomizations</c>. Token and editor colors go into <see cref="EditorTheme"/>; UI tokens
/// (<c>Shell.*</c>) into the application's resources, which come before the theme dictionaries'. Changes are live.
/// </summary>
public sealed partial class ShellHost
{
    private Editor.EditorTheme? _editorTheme;
    private string? _editorThemeFor;
    private readonly List<string> _uiOverrides = [];

    /// <summary>The user's color overrides for the active theme.</summary>
    public ColorOverrides ColorOverrides { get; private set; } = ColorOverrides.None;

    /// <summary>The active theme's editor colors with the user's over them.</summary>
    public Editor.EditorTheme EditorTheme
    {
        get
        {
            if (_editorTheme is null || _editorThemeFor != Theme.ActiveTheme)
            {
                _editorTheme = Editor.EditorTheme.For(Theme.ActiveTheme).WithOverrides(ColorOverrides.Tokens, ColorOverrides.Elements);
                _editorThemeFor = Theme.ActiveTheme;
            }

            return _editorTheme;
        }
    }

    private void InitializeColors()
    {
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(ColorCustomizations.WorkbenchKey) || e.Affects(ColorCustomizations.TokensKey))
            {
                Ui.Post(() =>
                {
                    ApplyColorCustomizations();
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                });
            }
        };
        Theme.Changed += (_, _) => ApplyColorCustomizations();
        ApplyColorCustomizations();
    }

    private void ApplyColorCustomizations()
    {
        var title = ThemeService.Choices.FirstOrDefault(c => c.Id == Theme.ActiveTheme)?.Title ?? Theme.ActiveTheme;
        ColorOverrides = ColorCustomizations.For(Theme.ActiveTheme, title,
            Workbench.Settings.GetValue(ColorCustomizations.WorkbenchKey), Workbench.Settings.GetValue(ColorCustomizations.TokensKey));
        _editorTheme = null;
        if (Avalonia.Application.Current is not { } app)
        {
            return;
        }

        // UI tokens: last time's go, so the theme's own come back; then this theme's overrides.
        foreach (var key in _uiOverrides)
        {
            app.Resources.Remove(key);
        }

        _uiOverrides.Clear();
        foreach (var (key, argb) in ColorOverrides.Ui)
        {
            app.Resources[key] = new SolidColorBrush(argb);
            _uiOverrides.Add(key);
        }
    }
}
