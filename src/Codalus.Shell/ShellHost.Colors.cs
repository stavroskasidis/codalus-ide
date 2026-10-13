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

    private (string File, DateTime Written, ColorOverrides Colors)? _themeLayer;

    /// <summary>A plugin theme's colors, read once per file version.</summary>
    private ColorOverrides ThemeLayer(Workbench.Plugins.ContributedTheme theme)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(theme.File);
            if (_themeLayer is { } cached && cached.File == theme.File && cached.Written == written)
            {
                return cached.Colors;
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(theme.File), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var colors = ThemeFiles.Read(document.RootElement);
            _themeLayer = (theme.File, written, colors);
            return colors;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Workbench.Log.AppendLine($"The theme '{theme.Label}' ({theme.File}) couldn't be read: {ex.Message}");
            return ColorOverrides.None;
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
        // A plugin's theme (API 1.0) is its colors over its base; the user's customizations go over both, by the
        // plugin theme's id or label as well as the base's.
        var contributed = Theme.ActiveContributed;
        var themeId = contributed?.Id ?? Theme.ActiveTheme;
        var title = contributed?.Label ?? ThemeService.Choices.FirstOrDefault(c => c.Id == Theme.ActiveTheme)?.Title ?? Theme.ActiveTheme;
        var user = ColorCustomizations.For(themeId, title,
            Workbench.Settings.GetValue(ColorCustomizations.WorkbenchKey), Workbench.Settings.GetValue(ColorCustomizations.TokensKey));
        ColorOverrides = contributed is null ? user : ThemeFiles.Merge(ThemeLayer(contributed), user);
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
