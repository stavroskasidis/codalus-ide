using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>Code Search's index and external items (M47, ADR 0078) on samples/LanguageFeatures.</summary>
public sealed partial class LanguageServiceTests
{
    [Fact]
    public async Task The_symbol_index_has_every_type_and_member_with_its_container_project_and_name_position()
    {
        var index = await fixture.Client.SymbolIndexAsync(null, Ct);
        var shapes = Assert.Single(index.Documents, d => d.Uri == Uri(Shapes));
        Assert.Equal("Lib", shapes.Project);
        Assert.Contains(index.Documents, d => d.Uri == Uri(Program) && d.Project == "App");
        Assert.Equal(index.Documents.Length, index.Documents.Select(d => d.Uri).Distinct().Count());

        var lines = await File.ReadAllLinesAsync(Shapes, Ct);
        var circle = Assert.Single(shapes.Symbols, s => s.Name == "Circle");
        Assert.Equal(SymbolKind.Class, circle.Kind);
        Assert.Equal("Lib", circle.Container);
        Assert.StartsWith("Circle", lines[circle.Line][circle.Character..], StringComparison.Ordinal);

        Assert.Contains(shapes.Symbols, s => s is { Name: "IShape", Kind: SymbolKind.Interface });
        Assert.Contains(shapes.Symbols, s => s is { Name: "Radius", Kind: SymbolKind.Property, Container: "Circle" });
        Assert.Contains(shapes.Symbols, s => s is { Name: "Area", Kind: SymbolKind.Method, Container: "Square" });
        Assert.Contains(shapes.Symbols, s => s is { Name: "Scale", Kind: SymbolKind.Method, Container: "Geometry" });
        var describe = shapes.Symbols.First(s => s is { Name: "Describe", Container: "Shape" });
        Assert.StartsWith("Describe(", lines[describe.Line][describe.Character..], StringComparison.Ordinal); // the name, not the doc comment above
        Assert.DoesNotContain(shapes.Symbols, s => s.Name is "precision" or "radius"); // parameters aren't declarations to find

        // One document only: what the shell asks after an edit.
        var one = await fixture.Client.SymbolIndexAsync([Uri(Shapes)], Ct);
        Assert.Equal([Uri(Shapes)], one.Documents.Select(d => d.Uri));
    }

    [Fact]
    public async Task External_items_are_referenced_types_in_metadata_documents()
    {
        var found = await fixture.Client.ExternalSymbolsAsync("StringBuilder", 20, Ct);
        var builder = Assert.Single(found, s => s.Name == "StringBuilder" && s.ContainerName!.StartsWith("System.Text", StringComparison.Ordinal));
        Assert.True(DocumentUris.IsMetadata(builder.Location.Uri), builder.Location.Uri);
        Assert.Equal("StringBuilder", found[0].Name); // the exact name first
        var text = await fixture.Client.MetadataTextAsync(builder.Location.Uri, Ct);
        Assert.Contains("class StringBuilder", text?.Text, StringComparison.Ordinal);

        // Camel-case humps; the solution's own types aren't external.
        Assert.Contains(await fixture.Client.ExternalSymbolsAsync("SB", 500, Ct), s => s.Name == "StringBuilder");
        Assert.DoesNotContain(await fixture.Client.ExternalSymbolsAsync("Circle", 50, Ct), s => s.Name == "Circle");
        Assert.True(SymbolIndexFeature.Matches("ioe", "InvalidOperationException"));
        Assert.False(SymbolIndexFeature.Matches("xyz", "InvalidOperationException"));
    }
}
