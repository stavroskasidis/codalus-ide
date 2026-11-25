using Codalus.LanguageServices.Host.Features;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>C# folding's regions and using groups found by pruned walks, as the walks of the whole tree found them.</summary>
public sealed class FoldingWalksTests
{
    private const string Source = """
        using System;
        using System.IO;

        namespace Outer
        {
            using System.Text;
            using System.Linq;

            #region Types
            /// <summary>
            /// A documented class.
            /// </summary>
            public class A
            {
                #region Members
                public void M()
                {
                    // one
                    // two
                    #if DEBUG
                    var x = 1;
                    #endif
                }
                #endregion
            }
            #endregion

            namespace Inner
            {
                using System.Collections;
                using System.Collections.Generic;
                class B { }
            }
        }
        """;

    [Fact]
    public async Task Regions_and_using_groups_are_those_of_the_whole_tree()
    {
        using var workspace = new AdhocWorkspace();
        var document = workspace.AddProject("P", LanguageNames.CSharp).AddDocument("A.cs", SourceText.From(Source));
        var ranges = await FoldingFeature.FoldingRangesAsync(document, TestContext.Current.CancellationToken);

        var root = (await document.GetSyntaxRootAsync(TestContext.Current.CancellationToken))!;
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        int Line(int position) => text.Lines.GetLineFromPosition(position).LineNumber;

        // The whole tree's walks, as before.
        var regions = root.DescendantTrivia(descendIntoTrivia: true).Select(t => t.GetStructure()).OfType<RegionDirectiveTriviaSyntax>()
            .Select(r => (Line(r.SpanStart), Line(r.GetRelatedDirectives().OfType<EndRegionDirectiveTriviaSyntax>().First().Span.End), r.EndOfDirectiveToken.LeadingTrivia.ToString().Trim()))
            .ToList();
        var usings = root.DescendantNodesAndSelf().Where(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Select(n => n is CompilationUnitSyntax unit ? unit.Usings : ((BaseNamespaceDeclarationSyntax)n).Usings)
            .Where(u => u.Count > 1).Select(u => (Line(u[0].SpanStart), Line(u[^1].Span.End))).ToList();

        Assert.Equal(2, regions.Count);
        Assert.Equal(3, usings.Count);
        Assert.Equal(regions.Select(r => (r.Item1, r.Item2)).Order(), ranges.Where(r => r.Kind == "region").Select(r => (r.StartLine, r.EndLine)).Order());
        Assert.Equal(["Types", "Members"], regions.Select(r => r.Item3));
        Assert.Equal(usings.Order(), ranges.Where(r => r.Kind == "imports").Select(r => (r.StartLine, r.EndLine)).Order());
    }
}
