using DaedalusStudio.LanguageServices.Host.Features;
using DaedalusStudio.LanguageServices.Protocol;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>M12a metadata navigation: original sources from portable PDBs, and Source Link URL mapping.</summary>
public sealed class PdbSourcesTests
{
    [Fact]
    public void Embedded_sources_come_from_the_assemblys_pdb()
    {
        // This test assembly is built with an embedded PDB and its sources embedded.
        var assembly = typeof(PdbSourcesTests).Assembly.Location;
        var source = PdbSources.Find(assembly, typeof(PdbSourcesTests).FullName!);

        Assert.NotNull(source);
        Assert.Equal(MetadataOrigins.Embedded, source.Origin);
        Assert.EndsWith("PdbSourcesTests.cs", source.Path, StringComparison.Ordinal);
        Assert.Contains("public sealed class PdbSourcesTests", source.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Assemblies_without_a_pdb_are_decompiled_instead()
    {
        // The SDK's reference assemblies have no PDB.
        var reference = typeof(object).Assembly.Location;
        Assert.Null(PdbSources.Find(reference, "System.Object.DoesNotExist"));
    }

    [Theory]
    [InlineData(@"C:\src\repo\src\Lib\Shapes.cs", "https://raw.githubusercontent.com/org/repo/abc123/src/Lib/Shapes.cs")]
    [InlineData(@"C:\src\repo\external\lib\File With Space.cs", "https://raw.githubusercontent.com/other/lib/def456/File%20With%20Space.cs")]
    [InlineData(@"D:\elsewhere\File.cs", null)]
    public void Source_link_maps_document_paths_to_urls(string path, string? expected)
    {
        const string map = """
            {
              "documents": {
                "C:\\src\\repo\\*": "https://raw.githubusercontent.com/org/repo/abc123/*",
                "C:\\src\\repo\\external\\lib\\*": "https://raw.githubusercontent.com/other/lib/def456/*"
              }
            }
            """;
        Assert.Equal(expected, PdbSources.MapSourceLink(map, path));
    }
}
