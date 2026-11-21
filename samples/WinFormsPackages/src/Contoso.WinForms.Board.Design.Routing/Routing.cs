using Contoso.WinForms.Board.Design;
using Microsoft.DotNet.DesignTools.Protocol.Endpoints;
using Microsoft.DotNet.DesignTools.Services;
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

/// <summary>
/// The server's side of the board designer's dialog, as DevExpress's <c>ContinueStartModalDialogRequestHandler</c> is: the
/// IDE's process asks it to continue, and it shows the dialog, then tells that process to stop its modal loop.
/// </summary>
[ExportRequestHandler("UITypeEditorEndpoint/ContinueStartModalDialog")]
public sealed class ContinueHandler : RequestHandler<ContinueRequest, ContinueResponse>
{
    public override ContinueResponse HandleRequest(ContinueRequest request)
    {
        var board = BoardDesigner.Pending ?? throw new InvalidOperationException("No board's designer asked for its dialog.");
        using (var form = new BoardSettingsForm(board, "From the board designer"))
        {
            _ = form.ShowDialog();
        }

        (ServiceProvider?.GetService(typeof(INotificationService)) as INotificationService)?.SendNotification("MessageLoopNotification/StopModalMessageLoop", new NoData());
        return new ContinueResponse();
    }
}

public sealed class ContinueRequest : Request
{
}

public sealed class ContinueResponse : Response
{
}
