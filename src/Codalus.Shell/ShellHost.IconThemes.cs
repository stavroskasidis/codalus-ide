using System.Text.Json;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.Theming;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// File icon themes from plugins (plugin API 1.0, M22a): <c>workbench.iconTheme</c> picks one, or the built-in icons
/// (<c>default</c>, the classic ones; <c>material</c>, Material Icon Theme's). The Solution Explorer, tabs and pickers take
/// their file and folder icons from it.
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
            Enum = ["default", "material", .. themes.Select(t => t.Id)],
            EnumDescriptions = ["Built in (classic)", "Material Icon Theme (built in)", .. themes.Select(t => t.Label)],
            Scope = SettingScope.User,
            Order = 2,
        }]);
        ApplyIconTheme();
    }

    private bool _iconThemeHooked;

    /// <summary>Themes whose SVG icons are being (or were) converted this session.</summary>
    private readonly HashSet<string> _iconsConverting = new(StringComparer.Ordinal);

    /// <summary>Converts a theme's SVG icons outside the shell (M22b), registers them, and applies the theme again.</summary>
    private async Task ConvertSvgIconsAsync(string label, string themeFile, IReadOnlyList<string> svgs)
    {
        if (PluginHostLocator.Resolve() is not { } host)
        {
            Workbench.Log.AppendLine($"The icon theme '{label}' has SVG icons, and the plugin host that converts them wasn't found.");
            return;
        }

        try
        {
            var icons = await Task.Run(() => IconThemeCompiler.CompileAsync([.. svgs.Distinct(StringComparer.Ordinal)], Paths.Cache, host, CancellationToken.None)).ConfigureAwait(false);
            foreach (var icon in icons.Where(i => i.Error is null))
            {
                Controls.IconSet.RegisterVector(icon.Path, icon.Ops, icon.Numbers, icon.Colors, icon.Size);
            }

            if (icons.Where(i => i.Error is not null).Select(i => $"{Path.GetFileName(i.Path)} ({i.Error})").ToList() is { Count: > 0 } failed)
            {
                Workbench.Log.AppendLine($"The icon theme '{label}' has SVG icons that can't be drawn, which use the built-in icons: {string.Join(", ", failed.Take(12))}{(failed.Count > 12 ? "…" : string.Empty)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            Workbench.Log.AppendLine($"The icon theme '{label}''s SVG icons couldn't be converted: {ex.Message}");
            return;
        }

        Ui.Post(() =>
        {
            if (Plugins.IconThemes.FirstOrDefault(t => t.File == themeFile) is { } current && current.Id == Workbench.Settings.GetString(IconThemeSetting, "default"))
            {
                ApplyIconTheme();
            }
        });
    }

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
        Controls.FileIcons.Material = id == "material";
        if (Plugins.IconThemes.FirstOrDefault(t => t.Id == id) is { File: { } file } theme)
        {
            try
            {
                var read = IconTheme.Read(file, path => Controls.IconSet.HasVector(path) ? Controls.IconSet.VectorPrefix + path : null);
                Controls.FileIcons.Theme = read;
                if (read.SvgFiles.Any(path => !Controls.IconSet.HasVector(path)) && _iconsConverting.Add(file))
                {
                    _ = ConvertSvgIconsAsync(theme.Label, file, read.SvgFiles);
                }
                else if (read.Skipped.Count > 0)
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
