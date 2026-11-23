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

    private static bool Shown<T>(Window window) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Any(v => v.IsEffectivelyVisible);
}
