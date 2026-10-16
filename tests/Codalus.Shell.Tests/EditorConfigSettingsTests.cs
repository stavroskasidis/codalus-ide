using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>The EditorConfig settings page (deferred from M12b).</summary>
public sealed class EditorConfigSettingsTests
{
    [AvaloniaFact]
    public async Task An_editorconfig_opens_as_options_whose_changes_edit_the_file_in_place()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "editorconfig");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ".editorconfig");
        await File.WriteAllTextAsync(path, "root = true\n\n# Every file.\n[*]\nindent_style = space\n\n[*.cs]\ncsharp_style_var_elsewhere = true:silent\nmy_own_key = kept\n", TestContext.Current.CancellationToken);

        // The page opens on the C# section, with the file's values and the known keys in groups.
        var document = f.Shell.OpenEditorConfigSettings(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document!.Rows.Count > 0);
        Assert.Equal("*.cs", document!.Section);
        Assert.Equal(["*", "*.cs"], document.Sections);
        Assert.Equal("true:silent", document.Rows.Single(r => r.Key == "csharp_style_var_elsewhere").Value);
        Assert.Contains(document.Rows, r => r.Group == "C# formatting");
        Assert.False(document.Rows.Single(r => r.Key == "indent_size").IsSet);

        // The search box filters by key or description.
        document.Filter = "indent_size";
        Assert.Equal("indent_size", Assert.Single(document.Rows).Key);
        document.Filter = string.Empty;

        // A choice in one section, a free value and a cleared one in another; saving writes only those lines.
        document.Rows.Single(r => r.Key == "csharp_style_var_elsewhere").Choice = EditorConfigOptionRow.NotSet;
        document.Rows.Single(r => r.Key == "indent_size").Value = "4";
        document.Section = "*";
        Assert.Equal("space", document.Rows.Single(r => r.Key == "indent_style").Value);
        document.Rows.Single(r => r.Key == "end_of_line").Choice = "lf";
        Assert.True(document.IsModified);
        Assert.Same(document, f.Shell.Layout.ActiveDocument);
        await f.Shell.SaveActiveDocumentAsync();
        Assert.False(document.IsModified);
        Assert.Equal(
            "root = true\n\n# Every file.\n[*]\nindent_style = space\nend_of_line = lf\n\n[*.cs]\nmy_own_key = kept\nindent_size = 4\n",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        // Opening it again shows the same page; View Code opens the file in the text editor.
        Assert.Same(document, f.Shell.OpenEditorConfigSettings(path));
        document.ViewCodeCommand.Execute(null);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is CodeEditorDocument { FilePath: { } open } && open == path);
    }
}
