using Codalus.Workbench.CustomTools;

namespace Codalus.Workbench.Tests;

/// <summary>The .resx editor's model (deferred from M11): string edits applied to the XML, everything else kept.</summary>
public sealed class ResxFileTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <root>
          <!-- The schema and headers stay as they are. -->
          <resheader name="resmimetype">
            <value>text/microsoft-resx</value>
          </resheader>
          <data name="Greeting" xml:space="preserve">
            <value>Hello</value>
            <comment>Shown on start</comment>
          </data>
          <data name="Logo" type="System.Resources.ResXFileRef, System.Windows.Forms">
            <value>Logo.png;System.Byte[], mscorlib</value>
          </data>
          <data name="Farewell" xml:space="preserve">
            <value>Bye</value>
          </data>
        </root>
        """;

    [Fact]
    public void Strings_are_edited_in_place_and_the_rest_is_kept()
    {
        var file = ResxFile.Parse(Sample);
        Assert.Equal(["Greeting", "Logo", "Farewell"], file.Entries.Select(e => e.Name));
        Assert.True(file.Entries[0] is { Value: "Hello", Comment: "Shown on start", IsString: true });
        Assert.False(file.Entries[1].IsString);

        file.Set("Greeting", "Hello, <world> & you", null);
        file.Rename("Farewell", "Goodbye");
        file.Set("Goodbye", "See you", "On exit");
        file.Set("Title", "Codalus", null);
        var text = file.Save();

        var again = ResxFile.Parse(text);
        Assert.Equal(["Greeting", "Logo", "Goodbye", "Title"], again.Entries.Select(e => e.Name));
        Assert.True(again.Entries[0] is { Value: "Hello, <world> & you", Comment: null });
        Assert.True(again.Entries[2] is { Value: "See you", Comment: "On exit" });
        Assert.Equal("Codalus", again.Entries[3].Value);
        Assert.Contains("<!-- The schema and headers stay as they are. -->", text, StringComparison.Ordinal);
        Assert.Contains("Logo.png;System.Byte[], mscorlib", text, StringComparison.Ordinal);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>", text, StringComparison.Ordinal);
        Assert.Contains("  <data name=\"Title\" xml:space=\"preserve\">\n    <value>Codalus</value>\n  </data>\n</root>", text, StringComparison.Ordinal);

        again.Remove("Greeting");
        Assert.DoesNotContain("Greeting", again.Save(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => again.Rename("Goodbye", "Title"));
        Assert.Throws<InvalidOperationException>(() => again.Set("Logo", "x", null));
    }

    [Fact]
    public void A_file_keeps_its_line_endings_on_every_os()
    {
        foreach (var newline in (string[])["\n", "\r\n"])
        {
            var file = ResxFile.Parse(Sample.ReplaceLineEndings(newline));
            file.Set("Title", "Codalus", "line one\nline two");
            var text = file.Save();
            Assert.Equal(text.ReplaceLineEndings(newline), text);
            Assert.Contains($"<data name=\"Title\" xml:space=\"preserve\">{newline}    <value>Codalus</value>", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_empty_file_takes_strings_and_names_are_checked()
    {
        var file = ResxFile.Empty();
        Assert.Empty(file.Entries);
        file.Set("First", "1", "the first");
        Assert.Equal("1", ResxFile.Parse(file.Save()).Entries.Single().Value);

        Assert.Null(ResxFile.Validate(["A", "B"]));
        Assert.Equal("There are two resources named A.", ResxFile.Validate(["A", "A"]));
        Assert.Equal("Every resource needs a name.", ResxFile.Validate(["A", " "]));
        Assert.Throws<System.Xml.XmlException>(() => ResxFile.Parse("<Project />"));
    }
}
