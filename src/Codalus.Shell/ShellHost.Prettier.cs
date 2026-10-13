using Codalus.Shell.Editor;
using Codalus.Workbench.Node;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// Prettier (M16d, ADR 0027): Format Document and format on save run the project's own Prettier for the files it
/// formats, when the project has it installed and configured (setting <c>web.prettier</c>: auto or off). Node.js is
/// the built-in one when installed, else the one on PATH.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Formats <paramref name="model"/> with its project's Prettier; false when Prettier doesn't apply (or failed, which is reported).</summary>
    private async Task<bool> FormatWithPrettierAsync(CodeDocument model)
    {
        if (Workbench.Settings.GetString(Prettier.Setting, "auto") == "off" || !Path.IsPathRooted(model.Path)
            || await Task.Run(() => Prettier.For(model.Path)).ConfigureAwait(true) is not { } prettier)
        {
            return false;
        }

        var node = Node.IsInstalled ? Node.NodePath : FindOnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");
        if (node is null)
        {
            LanguageLog.AppendLine($"[prettier] {model.Name}: Node.js isn't installed (install the web language servers, or Node.js).");
            return false;
        }

        var version = model.Version;
        var text = model.Text.Text;
        try
        {
            var formatted = await Prettier.FormatAsync(node, prettier, model.Path, text, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            if (version != model.Version || formatted == text)
            {
                return true;
            }

            // Only the changed middle is replaced, so the caret and the undo step stay where they belong.
            var prefix = 0;
            while (prefix < text.Length && prefix < formatted.Length && text[prefix] == formatted[prefix])
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < text.Length - prefix && suffix < formatted.Length - prefix && text[^(suffix + 1)] == formatted[^(suffix + 1)])
            {
                suffix++;
            }

            model.Text.Replace(prefix, text.Length - prefix - suffix, formatted.Substring(prefix, formatted.Length - prefix - suffix));
            LanguageLog.AppendLine($"[prettier] Formatted {model.Name} with {prettier}.");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            LanguageLog.AppendLine($"[prettier] {model.Name}: {ex.Message}");
            StatusBar.ShowMessage(Localizer.Format("msg.prettier.prettierCouldntFormat", "Prettier couldn't format {0}: {1}", model.Name, ex.Message.Split('\n')[0]), TimeSpan.FromSeconds(8));
            return true;
        }
    }

    private static string? FindOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder, executable))
            .FirstOrDefault(File.Exists);
}
