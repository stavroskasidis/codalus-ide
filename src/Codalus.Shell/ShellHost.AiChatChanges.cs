using System.Text;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Ai;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// AI Chat's side of ADR 0092: the files a question can @-mention, the chat's density, and the files a turn changed,
/// compared with what they were before it or put back.
/// </summary>
public sealed partial class ShellHost
{
    private const int MaxMentionableFiles = 20_000;

    private void InitializeAiChatChanges()
    {
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Affects(Codalus.Workbench.Ai.AiChat.DensitySetting))
            {
                Ui.Post(() => AiChat.OnDensityChanged());
            }
        };
    }

    public void ShowAiMessage(string message) => StatusBar.ShowMessage(message, TimeSpan.FromSeconds(5));

    public IReadOnlyList<string> MentionableFiles()
    {
        var root = Solutions.Solution?.Directory;
        if (root is null)
        {
            return [];
        }

        return [.. SearchableFiles()()
            .Where(f => IsInWorkspace(f, root))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxMentionableFiles)];
    }

    bool IAiChatHost.SuggestionsEnabled =>
        Workbench.Settings.GetBoolean(Codalus.Workbench.Ai.AiCompletions.EnabledSetting, false) || Workbench.Settings.GetBoolean(Codalus.Workbench.Ai.AiNextEdit.EnabledSetting, false);

    public string ChatDensity => Workbench.Settings.GetString(Codalus.Workbench.Ai.AiChat.DensitySetting, "compact") is "detailed" ? "detailed" : "compact";

    public Task SetChatDensityAsync(string density) =>
        Workbench.Settings.SetAsync(Codalus.Workbench.Ai.AiChat.DensitySetting, density, SettingScope.User, CancellationToken.None);

    /// <summary>The file's text before the turn, in a temporary file, beside the file as it is now (or as the turn left it).</summary>
    public async Task OpenAiChangeDiffAsync(AiChangedFile change)
    {
        if (ChangedFilePath(change) is not { } path)
        {
            return;
        }

        var folder = Path.Combine(Paths.State, "ai-changes", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12].ToLowerInvariant());
        Directory.CreateDirectory(folder);
        var before = Path.Combine(folder, Localizer.Format("msg.ai.beforeFile", "{0} (before)", Path.GetFileName(path)));
        await File.WriteAllTextAsync(before, change.OldText ?? "").ConfigureAwait(true);
        var after = path;
        if (!File.Exists(path) && OpenTextOf(path) is null)
        {
            after = Path.Combine(folder, Path.GetFileName(path));
            await File.WriteAllTextAsync(after, change.NewText).ConfigureAwait(true);
        }

        await OpenFileDiffAsync(before, after).ConfigureAwait(true);
    }

    /// <summary>
    /// Puts the file back as it was before the turn, after asking: into the editor's buffer when it's open (one undo step,
    /// unsaved), else on the disk; a file the turn created is deleted.
    /// </summary>
    public async Task<bool> RevertAiChangeAsync(AiChangedFile change)
    {
        if (ChangedFilePath(change) is not { } path)
        {
            return false;
        }

        var name = Path.GetFileName(path);
        var revert = Localizer.Get("msg.ai.revert", "Revert");
        var choice = await Dialogs.ShowAsync(new DialogRequest(
            Localizer.Format("msg.ai.revertTitle", "Revert {0}?", name),
            change.OldText is null
                ? Localizer.Format("msg.ai.revertDeletes", "{0} was created in this answer: reverting deletes it.", name)
                : Localizer.Format("msg.ai.revertRestores", "{0} goes back to what it was before this answer; later changes to it are lost.", name),
            [revert, Localizer.Get("msg.ai.cancel", "Cancel")])
        {
            DefaultButton = Localizer.Get("msg.ai.cancel", "Cancel"),
            IsDanger = true,
        }).ConfigureAwait(true);
        if (choice.Button != revert)
        {
            return false;
        }

        if (change.OldText is null)
        {
            // An open editor of it closes with it.
            Layout.CloseDocuments([.. Editors.Where(e => Codalus.Shell.Editor.SolutionPaths.Comparer.Equals(e.Model.Path, path))]);
            File.Delete(path);
            return true;
        }

        if (_models.TryGetValue(path, out var model))
        {
            model.Text.Replace(0, model.Text.TextLength, change.OldText);
            return true;
        }

        await File.WriteAllTextAsync(path, change.OldText).ConfigureAwait(true);
        return true;
    }

    /// <summary>The change's file, inside the workspace, or null.</summary>
    private string? ChangedFilePath(AiChangedFile change)
    {
        var root = Solutions.Solution?.Directory;
        try
        {
            return root is null ? null : WorkspacePath(change.Path, root);
        }
        catch (Codalus.Workbench.Ai.Ide.IdeToolException)
        {
            return null;
        }
    }
}
