using Contoso.WinForms.Board.Design;
using Microsoft.DotNet.DesignTools.TypeRouting;

namespace Contoso.WinForms.Board.Design.Routing;

/// <summary>The library's routes: the card's designer name, from its older design assembly, to the designer that replaced it.</summary>
[ExportTypeRoutingDefinitionProvider]
public sealed class RoutingDefinitionProvider : TypeRoutingDefinitionProvider
{
    public override IEnumerable<TypeRoutingDefinition> GetDefinitions() =>
    [
        RerouteDesigner<CardDesigner>("Contoso.Legacy.Design.CardDesigner"),
    ];
}
