using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Tests;

/// <summary>The EditorConfig settings page's model (deferred from M12b): keys edited in place, everything else kept.</summary>
public sealed class EditorConfigFileTests
{
    private const string Sample = """
        # top-most EditorConfig file
        root = true

        [*]
        indent_style = space
        indent_size = 4 # four columns

        # C# files
        [*.cs]
        csharp_style_var_elsewhere = false:silent

        [*.md]
        trim_trailing_whitespace = false

        """;

    [Fact]
    public void Keys_are_read_and_edited_in_their_sections_and_the_rest_stays()
    {
        var file = EditorConfigFile.Parse(Sample);
        Assert.Equal(["*", "*.cs", "*.md"], file.Sections);
        Assert.Equal("4", file.Get("*", "indent_size"));
        Assert.Equal("false:silent", file.Get("*.cs", "CSHARP_STYLE_VAR_ELSEWHERE"));
        Assert.Null(file.Get("*.cs", "indent_size"));

        file.Set("*", "indent_size", "2");
        file.Set("*.cs", "csharp_style_namespace_declarations", "file_scoped:warning");
        file.Set("*.md", "trim_trailing_whitespace", null);
        file.Set("*.{cs,vb}", "dotnet_sort_system_directives_first", "true");
        var text = file.Save();

        Assert.Contains("# top-most EditorConfig file", text, StringComparison.Ordinal);
        Assert.Contains("indent_size = 2\n\n# C# files\n[*.cs]", text, StringComparison.Ordinal);
        Assert.Contains("[*.cs]\ncsharp_style_var_elsewhere = false:silent\ncsharp_style_namespace_declarations = file_scoped:warning\n", text, StringComparison.Ordinal);
        Assert.Contains("[*.md]\n\n[*.{cs,vb}]\ndotnet_sort_system_directives_first = true\n", text, StringComparison.Ordinal);
        Assert.Equal("2", EditorConfigFile.Parse(text).Values("*")["indent_size"]);
    }

    [Fact]
    public void The_options_come_in_groups_with_their_values()
    {
        var options = EditorConfigFile.Options;
        Assert.Contains(options, o => o is { Key: "indent_style", Group: "Whitespace and files" } && o.Values!.Contains("tab"));
        Assert.Contains(options, o => o is { Key: "csharp_style_var_elsewhere", Group: "C# code style" });
        Assert.Contains(options, o => o is { Key: "dotnet_style_readonly_field", Group: ".NET code style" });
        Assert.Contains(options, o => o.Key == "indent_size" && o.Values is null);
        var empty = EditorConfigFile.Parse(string.Empty);
        empty.Set("*", "root", "true");
        Assert.Equal("[*]\nroot = true\n", empty.Save());
    }
}
