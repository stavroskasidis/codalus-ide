using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// File icon themes from plugins (plugin API 1.0, M22a): <c>workbench.iconTheme</c> picks one, or the built-in icons
/// (<c>default</c>). The Solution Explorer, tabs and pickers take their file and folder icons from it.
/// </summary>
public sealed partial class ShellHost
{
    public const string IconThemeSetting = "workbench.iconTheme";

    /// <summary>The setting, offering the built-in icons and the plugins' themes.</summary>
    private void RegisterIconThemes()
    {
        var themes = Plugins.IconThemes;
        Workbench.Settings.Register([SettingDescriptor.Create(IconThemeSetting, "\"default\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "File Icon Theme",
            Category = "Appearance",
            Description = "The icons of files and folders. Plugins can add icon themes.",
            Enum = ["default", .. themes.Select(t => t.Id)],
            EnumDescriptions = ["Built in", .. themes.Select(t => t.Label)],
            Scope = SettingScope.User,
            Order = 2,
        }]);
        ApplyIconTheme();
    }

    private bool _iconThemeHooked;

    private void ApplyIconTheme()
    {
        if (!_iconThemeHooked)
        {
            _iconThemeHooked = true;
            Workbench.Settings.Changed += (_, e) =>
            {
                if (e.Affects(IconThemeSetting))
                {
                    Ui.Post(ApplyIconTheme);
                }
            };
        }

        var id = Workbench.Settings.GetString(IconThemeSetting, "default");
        Controls.FileIcons.Theme = null;
        if (Plugins.IconThemes.FirstOrDefault(t => t.Id == id) is { File: { } file } theme)
        {
            try
            {
                var read = IconTheme.Read(file);
                Controls.FileIcons.Theme = read;
                if (read.Skipped.Count > 0)
                {
                    Workbench.Log.AppendLine($"The icon theme '{theme.Label}' has icons only drawn as raster images here; these use the built-in icons: {string.Join(", ", read.Skipped.Take(12))}{(read.Skipped.Count > 12 ? "…" : string.Empty)}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Workbench.Log.AppendLine($"The icon theme '{theme.Label}' couldn't be read: {ex.Message}");
            }
        }

        Explorer.Refresh();
    }
}
