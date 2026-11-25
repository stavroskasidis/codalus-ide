using System.Text.Json;
using Codalus.LanguageServices.Client;
using Codalus.LanguageServices.Protocol;

namespace Codalus.Workbench.Tests;

/// <summary>Diagnostics and locations already in the narrowed shape are left as they are, and the rest still normalized.</summary>
public sealed class LspNormalizerFastPathTests
{
    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static Diagnostic[] Published(string diagnostics) =>
        LspNormalizer.PublishDiagnostics(Json("""{"uri":"file:///a.cs","diagnostics":""" + diagnostics + "}"))
            .Deserialize(ProtocolJsonContext.Default.PublishDiagnosticsParams)!.Diagnostics;

    [Fact]
    public void Diagnostics_with_severities_and_string_codes_pass_and_others_are_normalized()
    {
        const string range = """{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}""";
        var narrowed = Published($$"""[{"range":{{range}},"severity":2,"code":"CS0168","message":"a"},{"range":{{range}},"severity":1,"message":"b"}]""");
        Assert.Equal(["CS0168", null], narrowed.Select(d => d.Code));
        Assert.Equal([2, 1], narrowed.Select(d => d.Severity));

        // A numeric code becomes a string; a missing or null severity is an error.
        var other = Published($$"""[{"range":{{range}},"severity":2,"code":42,"message":"a"},{"range":{{range}},"message":"b"},{"range":{{range}},"severity":null,"message":"c"}]""");
        Assert.Equal(["42", null, null], other.Select(d => d.Code));
        Assert.Equal([2, DiagnosticSeverity.Error, DiagnosticSeverity.Error], other.Select(d => d.Severity));
        Assert.Empty(LspNormalizer.PublishDiagnostics(Json("""{"uri":"file:///a.cs"}""")).Deserialize(ProtocolJsonContext.Default.PublishDiagnosticsParams)!.Diagnostics ?? []);
    }

    [Fact]
    public void Locations_pass_and_links_and_single_locations_are_normalized()
    {
        const string range = """{"start":{"line":1,"character":0},"end":{"line":1,"character":3}}""";
        Location[] Read(string json) => LspNormalizer.Result(Methods.Definition, Json(json)).Deserialize(ProtocolJsonContext.Default.LocationArray)!;

        Assert.Equal(["file:///a.cs", "file:///b.cs"], Read($$"""[{"uri":"file:///a.cs","range":{{range}}},{"uri":"file:///b.cs","range":{{range}}}]""").Select(l => l.Uri));
        Assert.Equal(["file:///c.cs"], Read($$"""[{"targetUri":"file:///c.cs","targetRange":{{range}},"targetSelectionRange":{{range}}}]""").Select(l => l.Uri));
        Assert.Equal(["file:///d.cs"], Read($$"""{"uri":"file:///d.cs","range":{{range}}}""").Select(l => l.Uri));
        Assert.Equal(["file:///e.cs"], Read($$"""[{"uri":null,"range":{{range}}},{"uri":"file:///e.cs","range":{{range}}}]""").Select(l => l.Uri));
    }
}
