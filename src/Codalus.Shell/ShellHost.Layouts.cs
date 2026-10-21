using System.Text.Json;
using Codalus.Shell.Docking;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Window layouts (M23a): named layouts saved and applied (Window → Save Window Layout, Apply Window Layout), full
/// screen, and distraction-free mode (no tool windows, toolbar or status bar, full screen; the same command returns to
/// the layout before). Layouts are kept in the state folder's <c>layouts.json</c>.
/// </summary>
public sealed partial class ShellHost
{
    private LayoutSnapshot? _beforeDistractionFree;
    private bool _beforeDistractionFreeFullScreen;

    private string LayoutsFile => Path.Combine(Paths.State, "layouts.json");

    public bool IsDistractionFree => _beforeDistractionFree is not null;

    /// <summary>The saved layouts by name.</summary>
    public Dictionary<string, LayoutSnapshot> SavedLayouts()
    {
        try
        {
            return File.Exists(LayoutsFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(LayoutsFile), ShellJsonContext.Default.DictionaryStringLayoutSnapshot) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Workbench.Log.AppendLine($"[layouts] {LayoutsFile}: {ex.Message}");
            return [];
        }
    }

    private void SaveLayouts(Dictionary<string, LayoutSnapshot> layouts)
    {
        Directory.CreateDirectory(Paths.State);
        File.WriteAllText(LayoutsFile, JsonSerializer.Serialize(layouts, ShellJsonContext.Default.DictionaryStringLayoutSnapshot));
    }

    /// <summary>Saves the window's layout under <paramref name="name"/> (asked for when null); the same name replaces it.</summary>
    public async Task SaveWindowLayoutAsync(string? name = null)
    {
        name ??= Plugins.Prompts is { } prompts
            ? await prompts.InputAsync("Save Window Layout", "A name for this layout", null, null, "Codalus IDE", CancellationToken.None).ConfigureAwait(true)
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var layouts = SavedLayouts();
        layouts[name.Trim()] = Layout.Capture(null);
        SaveLayouts(layouts);
        StatusBar.ShowMessage(Localizer.Format("msg.layouts.savedTheWindowLayout", "Saved the window layout \"{0}\".", name.Trim()), TimeSpan.FromSeconds(4));
    }

    /// <summary>Applies a saved layout (<paramref name="name"/>, or picked), or Default (the reset layout).</summary>
    public async Task ApplyWindowLayoutAsync(string? name = null)
    {
        var layouts = SavedLayouts();
        if (name is null)
        {
            var items = layouts.Keys.Order(StringComparer.CurrentCultureIgnoreCase).Select(n => new QuickPickItem(n) { Tag = n })
                .Append(new QuickPickItem("Default") { Detail = "The layout Codalus IDE starts with", Tag = string.Empty }).ToList();
            name = await Palette.ShowAsync(items, "Apply a window layout").ConfigureAwait(true) is { Tag: string picked } ? picked : null;
        }

        if (name is null)
        {
            return;
        }

        if (name.Length == 0)
        {
            Layout.ResetLayout();
        }
        else if (!layouts.TryGetValue(name, out var snapshot) || !Layout.Restore(snapshot))
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.layouts.theWindowLayoutCouldntBeApplied", "The window layout \"{0}\" couldn't be applied (it was saved by another version).", name));
        }
    }

    /// <summary>Deletes a saved layout (<paramref name="name"/>, or picked).</summary>
    public async Task DeleteWindowLayoutAsync(string? name = null)
    {
        var layouts = SavedLayouts();
        name ??= await Palette.ShowAsync([.. layouts.Keys.Order(StringComparer.CurrentCultureIgnoreCase).Select(n => new QuickPickItem(n) { Tag = n })], "Delete a window layout")
            .ConfigureAwait(true) is { Tag: string picked } ? picked : null;
        if (name is not null && layouts.Remove(name))
        {
            SaveLayouts(layouts);
        }
    }

    public void ToggleFullScreen()
    {
        if (Window is { } window)
        {
            window.IsFullScreen = !window.IsFullScreen;
        }
    }

    /// <summary>
    /// Distraction-free mode: only the documents, full screen, no toolbar or status bar. Again: the layout, toolbar,
    /// status bar and window state as they were.
    /// </summary>
    public void ToggleDistractionFree()
    {
        if (_beforeDistractionFree is { } before)
        {
            _beforeDistractionFree = null;
            Layout.Restore(before);
            Toolbar.IsVisible = true;
            StatusBar.IsVisible = Workbench.Settings.GetBoolean("workbench.statusBar.visible", true);
            if (Window is { } restoreWindow)
            {
                restoreWindow.IsFullScreen = _beforeDistractionFreeFullScreen;
            }
        }
        else
        {
            _beforeDistractionFree = Layout.Capture(null);
            foreach (var tool in Layout.OpenToolWindows.ToList())
            {
                Layout.CloseToolWindow(tool.Id);
            }

            Toolbar.IsVisible = false;
            StatusBar.IsVisible = false;
            _beforeDistractionFreeFullScreen = Window?.IsFullScreen ?? false;
            if (Window is { } window)
            {
                window.IsFullScreen = true;
            }
        }

        Workbench.Context.Set("distractionFree", IsDistractionFree);
    }
}
