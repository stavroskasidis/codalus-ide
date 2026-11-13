namespace Codalus.Workbench.Ai.Ide;

/// <summary>M60's tools for agents: Compose services and Aspire resources: listed with their state, started, stopped, restarted or debugged one at a time, and their logs.</summary>
public static class IdeServiceTools
{
    private const string Service =
        """
        "name":{"type":"string","description":"The service: a compose service's name (or its container's), or an Aspire resource's name. list_services lists them."},
        "kind":{"type":"string","enum":["compose","aspire"],"description":"Only when a compose service and an Aspire resource have the same name."}
        """;

    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } =
    [
        IdeTools.Read("list_services", "The services of the workspace with their state, as Codalus IDE's Containers and Aspire windows show them: the services of the solution's compose files (docker or podman, the user's own engine), with their containers, and the resources of the Aspire AppHost running in the IDE (projects, containers, executables) with their endpoints. Also names the AppHost projects that aren't running.", "{}"),
        IdeTools.Action("service_start", "Start a compose service (its stopped container, or compose up for that service, which may build or pull its image) or an Aspire resource (its start command), and return its state.", "{" + Service + "}", "name"),
        IdeTools.Action("service_stop", "Stop a compose service's container or an Aspire resource (its stop command), and return its state.", "{" + Service + "}", "name"),
        IdeTools.Action("service_restart", "Restart a compose service's container or an Aspire resource (its restart command), and return its state.", "{" + Service + "}", "name"),
        IdeTools.Action("service_debug", "Debug a service in Codalus IDE: a compose service is brought up and the debugger attaches to its .NET process (as the IDE's Compose Debug does); an Aspire project resource that the IDE runs gets the debugger attached to its process. Then set breakpoints, make the service reach them and debug_wait_for_stop.", "{" + Service + "}", "name"),
        IdeTools.Read("service_logs", "The end of a compose service's container output (stdout and stderr) or an Aspire resource's console output, as the Containers and Aspire windows show it.", "{" + Service + ""","lines":{"type":"integer","description":"At most this many lines from the end (default 200, at most 2000)."}}""", "name"),
        IdeTools.Read("service_traces", "An Aspire resource's traces, where the AppHost's resource service has them; otherwise says where they are (the Aspire dashboard).", "{" + Service + "}", "name"),
        IdeTools.Read("service_metrics", "An Aspire resource's metrics, where the AppHost's resource service has them; otherwise says where they are (the Aspire dashboard).", "{" + Service + "}", "name"),
    ];
}
