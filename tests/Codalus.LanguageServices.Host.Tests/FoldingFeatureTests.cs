using Codalus.LanguageServices.Host.Features;
using Codalus.LanguageServices.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// The C# and Visual Basic folding ranges' kinds: members are <see cref="FoldingKinds.Member"/>, which Collapse to
/// Definitions folds; namespaces, types, regions and local functions have none of the definition kinds, so they stay open.
/// </summary>
public sealed class FoldingFeatureTests
{
    private static async Task<(FoldingRange[] Ranges, SourceText Text)> FoldAsync(string language, string code)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.CurrentSolution.AddProject("Folding", "Folding", language);
        var text = SourceText.From(code);
        var document = project.AddDocument(language == LanguageNames.CSharp ? "Service.cs" : "Service.vb", text);
        return (await FoldingFeature.FoldingRangesAsync(document, TestContext.Current.CancellationToken), text);
    }

    /// <summary>The kind of the range that starts on the line holding <paramref name="marker"/> (and isn't a comment).</summary>
    private static string? KindAt((FoldingRange[] Ranges, SourceText Text) folding, string marker)
    {
        var offset = folding.Text.ToString().IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, marker);
        var line = folding.Text.Lines.GetLineFromPosition(offset).LineNumber;
        return Assert.Single(folding.Ranges, r => r.StartLine == line && r.Kind != FoldingKinds.Comment).Kind ?? "none";
    }

    [Fact]
    public async Task Csharp_members_are_definitions_and_their_containers_are_not()
    {
        var folding = await FoldAsync(LanguageNames.CSharp, """
            using System;
            using System.Threading;

            namespace Contoso.Services
            {
                public sealed class OrderService
                {
                    private readonly int _retries = 3;

                    /// <summary>Creates the service.</summary>
                    /// <param name="retries">How often to try.</param>
                    public OrderService(int retries)
                    {
                        _retries = retries;
                    }

                    public int Retries
                    {
                        get
                        {
                            return _retries;
                        }
                    }

                    #region Orders
                    public bool Place(string user, CancellationToken cancellationToken)
                    {
                        bool Check(string name)
                        {
                            return name.Length > 0;
                        }

                        return Check(user);
                    }
                    #endregion
                }
            }
            """);

        Assert.Equal(FoldingKinds.Imports, KindAt(folding, "using System;"));
        Assert.Equal("none", KindAt(folding, "namespace Contoso.Services"));
        Assert.Equal("none", KindAt(folding, "public sealed class OrderService"));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "public OrderService(int retries)"));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "public int Retries"));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "get"));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "public bool Place("));
        Assert.Equal("none", KindAt(folding, "bool Check(")); // local functions stay open, as in the classic IDE
        Assert.Equal(FoldingKinds.Region, KindAt(folding, "#region Orders"));
        Assert.Contains(folding.Ranges, r => r.Kind == FoldingKinds.Comment && r.CollapsedText == "/// Creates the service. …");
    }

    [Fact]
    public async Task Visual_basic_members_are_definitions_and_their_containers_are_not()
    {
        var folding = await FoldAsync(LanguageNames.VisualBasic, """
            Imports System
            Imports System.Threading

            Namespace Contoso.Services
                Public NotInheritable Class OrderService
                    Private ReadOnly _retries As Integer

                    Public Sub New(retries As Integer)
                        _retries = retries
                    End Sub

                    Public ReadOnly Property Retries As Integer
                        Get
                            Return _retries
                        End Get
                    End Property
                End Class
            End Namespace
            """);

        Assert.Equal(FoldingKinds.Imports, KindAt(folding, "Imports System"));
        Assert.Equal("none", KindAt(folding, "Namespace Contoso.Services"));
        Assert.Equal("none", KindAt(folding, "Public NotInheritable Class OrderService"));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "Public Sub New("));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "Public ReadOnly Property Retries"));
        Assert.Equal(FoldingKinds.Member, KindAt(folding, "Get"));
    }
}
