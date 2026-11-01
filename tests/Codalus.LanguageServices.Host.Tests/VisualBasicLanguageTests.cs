using Codalus.LanguageServices.Protocol;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>samples/VisualBasic in the real build and language hosts: three VB projects and a C# library, one workspace.</summary>
public sealed class VisualBasicLanguageFixture : LanguageFixture
{
    protected override (string Sample, string Solution, int Projects) Sample => ("VisualBasic", "VisualBasic.slnx", 4);
}

/// <summary>
/// M35 (ADR 0066): Visual Basic in the C# language host, on Roslyn's VB services: completion and Quick Info,
/// definitions across C# and VB, live diagnostics, classification (XML literals included), rename, formatting,
/// document symbols, folding, code actions and signature help.
/// </summary>
public sealed class VisualBasicLanguageTests(VisualBasicLanguageFixture vb) : IClassFixture<VisualBasicLanguageFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Program => vb.File("VbConsole", "Program.vb");

    private string Inventory => vb.File("VbLibrary", "Inventory.vb");

    private static string Uri(string path) => DocumentUris.FromPath(path);

    private async Task FreshAsync(string path)
    {
        var disk = await File.ReadAllTextAsync(path, Ct);
        string current;
        try
        {
            current = vb.Text(path).Text;
        }
        catch (KeyNotFoundException)
        {
            await vb.OpenAsync(path);
            return;
        }

        if (current != disk)
        {
            await vb.EditAsync(path, current, disk);
        }
    }

    private async Task<IReadOnlyList<string>> LabelsAsync(string path, string marker, int delta)
    {
        var list = await vb.Client.CompletionAsync(Uri(path), vb.At(path, marker, delta), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct);
        return [.. list.Items.Select(i => i.Label)];
    }

    [Fact]
    public async Task Completion_and_quick_info_know_vb_and_the_csharp_library()
    {
        await FreshAsync(Program);
        await vb.EditAsync(Program, "Console.WriteLine(inventory.ToXml().ToString())", "Console.WriteLine(inventory.ToXml().ToString())\n        formatter.");
        var members = await LabelsAsync(Program, "formatter.\n", "formatter.".Length);
        Assert.Contains("Format", members); // a C# method, through the project reference
        Assert.Contains("Currency", members);

        var keywords = await LabelsAsync(Program, "Dim inventory", 0);
        Assert.Contains("Dim", keywords);

        // Enter after committing an item also starts a new line in Visual Basic (Roslyn's default there; never in C#).
        var dim = (await vb.Client.CompletionAsync(Uri(Program), vb.At(Program, "Dim inventory", 0), new CompletionContext(CompletionTriggerKind.Invoked, null), Ct)).Items.Single(i => i.Label == "Dim");
        Assert.Equal(EnterKeyRules.Always, dim.EnterKeyRule);

        // Quick Info in Visual Basic's own syntax, with the documentation comment.
        var hover = await vb.Client.HoverAsync(Uri(Program), vb.At(Program, "formatter.Format", "formatter.For".Length), Ct);
        Assert.Contains("```vb\nFunction PriceFormatter.Format(name As String, price As Decimal) As String", hover?.Contents.Value, StringComparison.Ordinal);
        Assert.Contains("Tea: 3.00 EUR", hover?.Contents.Value, StringComparison.Ordinal);
        await FreshAsync(Program);
    }

    [Fact]
    public async Task Definitions_go_across_vb_and_csharp_and_references_come_back()
    {
        await FreshAsync(Program);
        var csharp = Assert.Single(await vb.Client.DefinitionAsync(Uri(Program), vb.At(Program, "New PriceFormatter", "New Price".Length), Ct));
        Assert.EndsWith("PriceFormatter.cs", csharp.Uri, StringComparison.Ordinal);
        var vbType = Assert.Single(await vb.Client.DefinitionAsync(Uri(Program), vb.At(Program, "New Inventory", "New Inv".Length), Ct));
        Assert.EndsWith("Inventory.vb", vbType.Uri, StringComparison.Ordinal);

        await FreshAsync(Inventory);
        var references = await vb.Client.ReferencesAsync(Uri(Inventory), vb.At(Inventory, "Public Function Total", "Public Function T".Length), includeDeclaration: false, Ct);
        Assert.Contains(references, r => r.Uri.EndsWith("InventoryTests.vb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Errors_are_reported_and_fixed_live()
    {
        await FreshAsync(Program);
        await vb.EditAsync(Program, "inventory.Add(New Product(\"Tea\", 3D))", "inventory.Ad(New Product(\"Tea\", 3D))");
        await LanguageFixture.WaitUntilAsync(() => vb.Languages.Diagnostics.Get(Program, "csharp") is { } d && d.Items.Any(x => x.Code == "BC30456"), TimeSpan.FromSeconds(60));
        await FreshAsync(Program);
        await LanguageFixture.WaitUntilAsync(() => vb.Languages.Diagnostics.Get(Program, "csharp") is { } d && d.Items.All(x => x.Severity != DiagnosticSeverity.Error), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Classification_covers_keywords_types_and_xml_literals()
    {
        await FreshAsync(Inventory);
        var tokens = await vb.Client.SemanticTokensAsync(Uri(Inventory), null, Ct);
        Assert.NotEmpty(tokens.Data);
        var legend = SemanticTokensLegendOf();
        var kinds = Decode(tokens.Data).Select(t => legend[t.Type]).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("keyword", kinds);
        Assert.Contains("class", kinds);
        Assert.Contains(kinds, k => k.StartsWith("xml", StringComparison.Ordinal)); // the XML literal's names and delimiters
    }

    [Fact]
    public async Task Rename_formatting_outline_folding_and_signature_help()
    {
        await FreshAsync(Inventory);
        var rename = await vb.Client.RenameAsync(Uri(Inventory), vb.At(Inventory, "Public Function Total", "Public Function T".Length), "Sum", Ct);
        Assert.NotNull(rename);
        var files = rename!.DocumentChanges.Select(c => c.TextDocument.Uri).ToList();
        Assert.Contains(files, f => f.EndsWith("Inventory.vb", StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith("InventoryTests.vb", StringComparison.Ordinal));

        await vb.EditAsync(Inventory, "        _products.Add(product)", "            _products.Add(   product )");
        var edits = await vb.Client.FormatAsync(Uri(Inventory), null, new FormattingOptions(4, true), Ct);
        Assert.NotEmpty(edits);
        await FreshAsync(Inventory);

        var symbols = await vb.Client.DocumentSymbolsAsync(Uri(Inventory), Ct);
        var type = Assert.Single(symbols, s => s.Name == "Inventory");
        Assert.Contains(type.Children ?? [], c => c.Name.StartsWith("Total", StringComparison.Ordinal));
        Assert.Contains(type.Children ?? [], c => c.Name.StartsWith("Products", StringComparison.Ordinal));

        var folding = await vb.Client.FoldingRangesAsync(Uri(Inventory), Ct);
        Assert.True(folding.Length >= 4, "folding: " + folding.Length);

        await vb.EditAsync(Inventory, "_products.Add(product)", "_products.Add(product)\n        _products.Insert(");
        var signature = await vb.Client.SignatureHelpAsync(Uri(Inventory), vb.At(Inventory, "_products.Insert(", "_products.Insert(".Length), new SignatureHelpContext(2, "(", false), Ct);
        Assert.NotNull(signature);
        // The list's type argument is the call site's: List(Of Product), not List(Of T).
        Assert.Contains(signature!.Signatures, s => s.Label == "Sub List(Of Product).Insert(index As Integer, item As Product)");
        await FreshAsync(Inventory);
    }

    [Fact]
    public async Task Code_actions_offer_vb_fixes()
    {
        await FreshAsync(Program);
        await vb.EditAsync(Program, "Imports CsLibrary\n", "Imports CsLibrary\nImports System.Text\n");
        await LanguageFixture.WaitUntilAsync(() => vb.Languages.Diagnostics.Get(Program, "csharp") is { } d && d.Items.Any(x => x.Code == "IDE0005"), TimeSpan.FromSeconds(90));
        var diagnostic = vb.Languages.Diagnostics.Get(Program, "csharp")!.Items.First(x => x.Code == "IDE0005");
        var actions = await vb.Client.CodeActionsAsync(Uri(Program), diagnostic.Range, [diagnostic], Ct);
        Assert.Contains(actions, a => a.Title.Contains("Import", StringComparison.OrdinalIgnoreCase));
        await FreshAsync(Program);
    }

    [Fact]
    public async Task Rename_file_to_match_its_type_is_a_rename_of_the_file()
    {
        await FreshAsync(Inventory);
        await vb.EditAsync(Inventory, "Public Class Inventory", "Public Class InventoryList");
        var at = vb.At(Inventory, "Public Class InventoryList", "Public Class Inv".Length);
        var actions = await vb.Client.CodeActionsAsync(Uri(Inventory), new Protocol.Range(at, at), [], Ct);
        var renameFile = Assert.Single(actions, a => a.Title.Contains("Rename file to", StringComparison.Ordinal) && a.Title.Contains("InventoryList.vb", StringComparison.Ordinal));
        var edit = (await vb.Client.ResolveCodeActionAsync(renameFile, Ct)).Edit!;
        var operation = Assert.Single(edit.ResourceOperations);
        Assert.Equal(ResourceOperationKinds.Rename, operation.Kind);
        Assert.Equal(Uri(Inventory), operation.Uri);
        Assert.Equal(Uri(Path.Combine(Path.GetDirectoryName(Inventory)!, "InventoryList.vb")), operation.NewUri);
        await FreshAsync(Inventory);
    }

    [Fact]
    public async Task Enter_adds_the_end_of_a_block_and_fixes_keyword_casing_and_xml_tags_close()
    {
        await FreshAsync(Inventory);
        var options = new FormattingOptions(4, true);

        // "public sub Clear()" then Enter: the keywords take their casing, and End Sub follows the caret's line.
        await vb.EditAsync(Inventory, "    Public Function Total() As Decimal", "    public sub Clear()\n        \n    Public Function Total() As Decimal");
        var edits = await vb.Client.FormatOnTypeAsync(Uri(Inventory), vb.At(Inventory, "public sub Clear()\n        ", "public sub Clear()\n        ".Length), "\n", options, Ct);
        Assert.Contains(edits, e => e.NewText == "Public");
        Assert.Contains(edits, e => e.NewText == "Sub");
        var end = Assert.Single(edits, e => e.NewText.Contains("End Sub", StringComparison.Ordinal));
        Assert.Equal("    End Sub\n", end.NewText);
        Assert.Equal(vb.At(Inventory, "    Public Function Total").Line, end.Range.Start.Line);
        await FreshAsync(Inventory);

        // An If block in a method: End If, indented like the If.
        await vb.EditAsync(Inventory, "        _products.Add(product)", "        If product.Price > 100D Then\n            \n        _products.Add(product)");
        edits = await vb.Client.FormatOnTypeAsync(Uri(Inventory), vb.At(Inventory, "100D Then\n            ", "100D Then\n            ".Length), "\n", options, Ct);
        Assert.Equal("        End If\n", Assert.Single(edits).NewText);
        await FreshAsync(Inventory);

        // A block that already has its end gets nothing.
        edits = await vb.Client.FormatOnTypeAsync(Uri(Inventory), vb.At(Inventory, "        _products.Add(product)"), "\n", options, Ct);
        Assert.DoesNotContain(edits, e => e.NewText.Contains("End", StringComparison.Ordinal));

        // > closing an XML literal's start tag adds the end tag.
        await vb.EditAsync(Inventory, "        _products.Add(product)", "        Dim note = <note>\n        _products.Add(product)");
        edits = await vb.Client.FormatOnTypeAsync(Uri(Inventory), vb.At(Inventory, "<note>", "<note>".Length), ">", options, Ct);
        Assert.Equal("</note>", Assert.Single(edits).NewText);
        await FreshAsync(Inventory);
    }

    [Fact]
    public async Task Vb_tests_are_found_from_source_in_the_root_namespace()
    {
        var tests = vb.File("VbTests", "InventoryTests.vb");
        await FreshAsync(tests);
        var result = await vb.Client.TestCandidatesAsync([Uri(tests)], Ct);
        var candidates = Assert.Single(result.Documents).Candidates;
        var type = Assert.Single(candidates, c => c.Kind == TestCandidateKinds.Class);
        Assert.Equal(("InventoryTests", "VbTests.VbTests.InventoryTests"), (type.Name, type.Type)); // RootNamespace + Namespace
        Assert.Equal(["Total_adds_the_prices", "ToXml_lists_every_product"], candidates.Where(c => c.Kind == TestCandidateKinds.Method).Select(c => c.Name));
    }

    private static string[] SemanticTokensLegendOf() => [.. TokenTypes.All];

    private static IEnumerable<(int Line, int Start, int Length, int Type)> Decode(int[] data)
    {
        int line = 0, start = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            yield return (line, start, data[i + 2], data[i + 3]);
        }
    }
}

/// <summary>
/// A Visual Basic file no loaded project has (an untrusted folder, or one opened before its project loads) gets its
/// document symbols from its own text, parsed as Visual Basic. Miscellaneous Files is a C# project: it parsed the file
/// as C#, listed no types, and the navigation bar hid until the project came (macOS CI, run 41284207922).
/// </summary>
public sealed class UntrustedVisualBasicTests(UntrustedVisualBasicFixture vb) : IClassFixture<UntrustedVisualBasicFixture>
{
    [Fact]
    public async Task A_file_outside_any_loaded_project_lists_its_visual_basic_symbols()
    {
        var product = vb.File("VbLibrary", "Product.vb");
        await vb.OpenAsync(product);

        var symbols = await vb.Client.DocumentSymbolsAsync(DocumentUris.FromPath(product), TestContext.Current.CancellationToken);
        var type = Assert.Single(symbols);
        Assert.Equal(("Product", SymbolKind.Class), (type.Name, type.Kind));
        Assert.Contains(type.Children ?? [], s => s.Name == "Price" && s.Kind == SymbolKind.Property);
    }
}
