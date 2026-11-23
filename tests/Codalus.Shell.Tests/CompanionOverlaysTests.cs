using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// The setup and the start window stay above the main window, which owns them: the notifications, the palette and the
/// in-window dialogs that open while one is open are drawn in it, not in the main window under it (CompanionOverlays).
/// </summary>
public sealed class CompanionOverlaysTests
{
    [AvaloniaFact]
    public async Task A_dialog_opened_from_the_setup_shows_over_it_and_Escape_cancels_only_the_dialog()
    {
        // The .NET install's choice (the setup's Tools step) opened in the main window, under the setup (macOS, M49's check).
        await using var f = ShellFixture.Create();
        f.Shell.ShowSetup();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var setup = f.Shell.SetupWindowView!;
        var main = (Window)f.Shell.Window!;

        var answer = f.Shell.Dialogs.ConfirmAsync("Install .NET 11.0", "The .NET 11.0 runtime, for this user.", "Install");
        await ShellFixture.PumpUntil(() => Shown<DialogView>(setup), describe: () => $"the setup shows no dialog; the main window {(Shown<DialogView>(main) ? "does" : "doesn't either")}");
        Assert.False(Shown<DialogView>(main));
        await ShellFixture.PumpUntil(() => setup.FocusManager?.GetFocusedElement() is Visual focused && focused.FindAncestorOfType<DialogView>() is not null,
            describe: () => $"the focus is on {setup.FocusManager?.GetFocusedElement()}, not in the dialog");

        // Escape is the dialog's, not the setup's Skip.
        setup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        setup.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        ShellFixture.Pump();
        Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(f.Shell.IsSetupOpen);

        // With the setup closed, dialogs are the main window's again.
        f.Shell.SetupModel!.SkipCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        var again = f.Shell.Dialogs.ConfirmAsync("Install .NET 11.0", "The .NET 11.0 runtime, for this user.", "Install");
        await ShellFixture.PumpUntil(() => Shown<DialogView>(main));
        f.Shell.Dialogs.Cancel();
        Assert.False(await again.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task Connect_opened_with_the_start_window_open_shows_over_it()
    {
        // The setup's "Connect Claude…" from Help, with the start window open: Connect opened under the start window.
        await using var f = ShellFixture.Create();
        f.Shell.ShowStartWindow();
        await ShellFixture.PumpUntil(() => f.Shell.IsStartWindowOpen);
        var start = Assert.IsType<StartWindow>(f.Shell.OverlayWindow);
        var main = (Window)f.Shell.Window!;

        var connecting = f.Shell.ConnectExternalAgentAsync("claude");
        await ShellFixture.PumpUntil(() => Shown<ConnectAgentView>(start), describe: () => $"the start window shows no Connect; the main window {(Shown<ConnectAgentView>(main) ? "does" : "doesn't either")}");
        Assert.False(Shown<ConnectAgentView>(main));
        Assert.True(f.Shell.IsStartWindowOpen);

        f.Shell.ConnectAgent.Cancel();
        Assert.Null(await connecting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        ShellFixture.Pump();
        Assert.False(Shown<ConnectAgentView>(start));
    }

    [AvaloniaFact]
    public async Task A_notification_shows_over_the_setup_and_back_in_the_main_window_after_it()
    {
        await using var f = ShellFixture.Create();
        f.Shell.ShowSetup();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var setup = f.Shell.SetupWindowView!;
        var main = (Window)f.Shell.Window!;

        // A warning stays until it's closed (an information hides after a few seconds, which a loaded runner can take).
        f.Shell.Workbench.Notifications.Warning("The .NET 11.0 runtime isn't installed.");
        await ShellFixture.PumpUntil(() => Shown<NotificationCard>(setup), describe: () => $"the setup shows no notification; the main window {(Shown<NotificationCard>(main) ? "does" : "doesn't either")}");
        Assert.False(Shown<NotificationCard>(main));

        f.Shell.SetupModel!.SkipCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !f.Shell.IsSetupOpen);
        await ShellFixture.PumpUntil(() => Shown<NotificationCard>(main));
    }

    /// <summary>
    /// A command the IDE runs in the Terminal while the setup is open (Linux: git or .NET from the distribution's packages,
    /// under sudo) ran in the main window's Terminal, under the setup, where its password prompt couldn't be seen. It
    /// shows over the setup now, with the focus in it and its own size, until it ends; Hide leaves it running in the
    /// Terminal tool window, and Show Terminal brings it back while the setup covers that.
    /// </summary>
    [AvaloniaFact]
    public async Task A_command_in_the_terminal_shows_over_the_setup_until_it_ends()
    {
        await using var f = ShellFixture.Create();
        f.Shell.ShowSetup();
        await ShellFixture.PumpUntil(() => f.Shell.IsSetupOpen);
        var setup = f.Shell.SetupWindowView!;
        var main = (Window)f.Shell.Window!;

        var exit = f.Shell.RunAgentTerminalAsync("Install Git", WaitsForALine(exitCode: 4), TestContext.Current.CancellationToken);
        var card = f.Shell.TerminalCard;
        await ShellFixture.PumpUntil(() => Shown<TerminalCardView>(setup), 30, () => $"the setup shows no terminal card (open: {card.IsOpen}); the main window {(Shown<TerminalCardView>(main) ? "does" : "doesn't either")}");
        Assert.False(Shown<TerminalCardView>(main));
        Assert.Equal("Install Git", card.Title);
        var session = card.Session!;
        var control = setup.GetVisualDescendants().OfType<TerminalCardView>().Single().GetVisualDescendants().OfType<Codalus.Shell.Terminal.TerminalControl>().Single();
        await ShellFixture.PumpUntil(() => ReferenceEquals(setup.FocusManager?.GetFocusedElement(), control), describe: () => $"the focus is on {setup.FocusManager?.GetFocusedElement()} in {(setup.FocusManager?.GetFocusedElement() is Visual v ? TopLevel.GetTopLevel(v)?.GetType().Name : "no window")}, not in the card's terminal (visible {control.IsEffectivelyVisible}, focusable {control.Focusable}, focused {control.IsFocused}, in the tree {control.IsAttachedToVisualTree()})");
        await ShellFixture.PumpUntil(() => session.Terminal.Columns == control.Columns, describe: () => $"the session is {session.Terminal.Columns} columns wide, the card {control.Columns}");
        Assert.Same(control, session.SizeOwner);

        // Escape goes to the command, not to the setup's Skip.
        setup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        setup.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        ShellFixture.Pump();
        Assert.True(f.Shell.IsSetupOpen);

        // Hide: the card closes, the command goes on; Show Terminal brings it back while the setup covers the Terminal.
        card.HideCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !Shown<TerminalCardView>(setup));
        Assert.Null(session.ExitCode);
        Assert.Null(session.SizeOwner);
        f.Shell.ShowTerminalForCommand();
        await ShellFixture.PumpUntil(() => Shown<TerminalCardView>(setup) && card.Session == session);

        // The command ends (the user answered): the card closes by itself, and its exit code is the caller's.
        await session.WhenStartedAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        session.Write(OperatingSystem.IsWindows() ? "\r" : "\n");
        await ShellFixture.PumpUntil(() => exit.IsCompleted && !card.IsOpen, 30, () => $"exited: {(session.ExitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no")}");
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(4, await exit);
        }

        ShellFixture.Pump();
        Assert.False(Shown<TerminalCardView>(setup));
        Assert.True(f.Shell.IsSetupOpen);
    }

    /// <summary>With nothing over the Terminal tool window, a command shows there alone, as before.</summary>
    [AvaloniaFact]
    public async Task A_command_in_the_terminal_shows_in_the_tool_window_alone_when_nothing_covers_it()
    {
        await using var f = ShellFixture.Create();
        Assert.False(f.Shell.TerminalIsCovered);
        var exit = f.Shell.RunAgentTerminalAsync("Install Git", WaitsForALine(exitCode: 0), TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => f.Shell.Terminals.Sessions.Any(s => s.Name == "Install Git"));
        var session = f.Shell.Terminals.Sessions.Single(s => s.Name == "Install Git");
        ShellFixture.Pump();
        Assert.False(f.Shell.TerminalCard.IsOpen);
        Assert.Null(session.SizeOwner);
        await session.WhenStartedAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        session.Write(OperatingSystem.IsWindows() ? "\r" : "\n");
        await ShellFixture.PumpUntil(() => exit.IsCompleted, 30);
    }

    /// <summary>A program that prints a prompt, waits for a line, then ends with <paramref name="exitCode"/> (Windows: cmd's pause).</summary>
    private static Codalus.Rpc.ProcessLaunchInfo WaitsForALine(int exitCode) => OperatingSystem.IsWindows()
        ? new("cmd.exe", ["/c", $"pause & exit {exitCode}"])
        : new("/bin/sh", ["-c", $"printf 'Password: '; read line; exit {exitCode}"]);

    private static bool Shown<T>(Window window) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Any(v => v.IsEffectivelyVisible);
}
