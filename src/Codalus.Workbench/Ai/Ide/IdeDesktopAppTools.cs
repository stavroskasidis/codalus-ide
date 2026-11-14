namespace Codalus.Workbench.Ai.Ide;

/// <summary>
/// M60b's tools for agents (ADR 0099): the desktop app being built, an Avalonia app (or a WPF app on Windows) that Codalus
/// IDE runs or debugs, reached through the live agent the Live Visual Tree uses (M19b, M40b). Its windows with a picture
/// of each and its control tree are Read; input sent to a control (click, text, keys, scroll) is an Action under
/// <c>ai.agent.ideActions</c> with no approval of its own (D52). Input goes to the control inside the app: the user's
/// pointer never moves and their focus stays where it is.
/// </summary>
public static class IdeDesktopAppTools
{
    private const string Window =
        """
        "window":{"type":"integer","description":"A window's id from desktop_app_windows; the app's first window by default."}
        """;

    /// <summary>The group's tools, in the order agents see them.</summary>
    public static IReadOnlyList<IdeToolDefinition> Tools { get; } =
    [
        IdeTools.Read("desktop_app_windows", "The windows of the desktop app Codalus IDE runs or debugs (an Avalonia app, or a WPF app on Windows, started with run_project or debug_start), through its live agent: each window's id, title, class and size, with a PNG picture of each (the window's own rendering: it works for a window that is covered, or off-screen). Waits for an app that is starting.", "{" + Window + ""","pictures":{"type":"boolean","description":"Include the pictures (default true)."}}"""),
        IdeTools.Read("desktop_app_controls", "The control tree of a window of the desktop app Codalus IDE runs or debugs: each control's id, type, name, the text it shows, its state (disabled, hidden, focused, checked, selected, read-only, value=…) and its bounds in the window (x,y width x height, device-independent pixels, as in the picture), indented by depth. Use the ids with desktop_app_input.", "{" + Window + ""","filter":{"type":"string","description":"Only the controls whose type, name or text contains this text (case-insensitive), with their ancestors."}}"""),
        IdeTools.Action("desktop_app_input", "Send input to a control of the desktop app Codalus IDE runs or debugs, inside the app: the user's pointer doesn't move and their focus stays where it is. click invokes, toggles, selects or expands the control (or the nearest control around it that can be); text types into its text box (at the end, or in place of its text with replace); keys presses keys on it (Enter, Escape, Tab, Down, Ctrl+A; WPF takes keys without modifiers); scroll moves its scroll viewer. Returns what was done; read the result with desktop_app_controls or desktop_app_windows.", """{"control":{"type":"integer","description":"The control's id from desktop_app_controls."},"name":{"type":"string","description":"Or the control's name (x:Name), in any window."},"kind":{"type":"string","enum":["click","text","keys","scroll"]},"text":{"type":"string","description":"With text: what to type."},"replace":{"type":"boolean","description":"With text: replace the box's text instead of typing at its end."},"keys":{"type":"string","description":"With keys: chords separated by spaces, e.g. 'Ctrl+A Delete' or 'Tab Enter'."},"direction":{"type":"string","enum":["up","down","left","right","pageUp","pageDown","top","bottom","intoView"],"description":"With scroll: where to (intoView brings the control itself into view)."},"amount":{"type":"integer","description":"With scroll: how many lines or pages (default 3 lines, 1 page)."}}""", "kind"),
    ];
}
