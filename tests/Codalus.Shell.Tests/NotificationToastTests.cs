using Avalonia.Headless.XUnit;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Notifications;

namespace Codalus.Shell.Tests;

/// <summary>
/// Informational toasts hide after a while, but not under the pointer (nor just after it leaves), not once the user
/// clicked in one, and not when they ask for a choice: those stay until dismissed or hidden with Escape.
/// </summary>
public sealed class NotificationToastTests
{
    [Fact]
    public void A_toast_whose_time_is_up_stays_under_the_pointer_a_moment_after_it_leaves_and_once_pinned()
    {
        var service = new NotificationService();
        var toast = new NotificationViewModel(service.Info("Restored the packages."), service);
        var now = DateTime.UtcNow;
        Assert.Null(NotificationsViewModel.ToastExpiry(toast, now));

        toast.IsHovered = true;
        Assert.Equal(NotificationsViewModel.HoverLinger, NotificationsViewModel.ToastExpiry(toast, now));

        toast.IsHovered = false;
        var left = NotificationsViewModel.ToastExpiry(toast, DateTime.UtcNow);
        Assert.NotNull(left);
        Assert.InRange(left.Value, TimeSpan.Zero, NotificationsViewModel.HoverLinger);
        Assert.Null(NotificationsViewModel.ToastExpiry(toast, DateTime.UtcNow + NotificationsViewModel.HoverLinger));

        toast.IsPinned = true;
        toast.IsHovered = true;
        Assert.Null(NotificationsViewModel.ToastExpiry(toast, now)); // pinned: never hidden by its timer, so none to wait for
    }

    [AvaloniaFact]
    public async Task Hovered_pinned_and_question_toasts_outlast_a_plain_one()
    {
        var service = new NotificationService();
        using var notifications = new NotificationsViewModel(service, () => 1);
        service.Info("Hovered.");
        service.Info("Pinned.");
        await ShellFixture.PumpUntil(() => notifications.Toasts.Count == 2);
        notifications.Toasts[0].IsHovered = true;
        notifications.Toasts[1].IsPinned = true;

        // The plain one comes last: when its timer has hidden it, the others' (one second each too) have fired.
        service.Info("Plain.");
        await ShellFixture.PumpUntil(() => notifications.Toasts.Count == 3);
        await ShellFixture.PumpUntil(() => notifications.Toasts.All(t => t.Message != "Plain."), seconds: 10);
        Assert.Equal(["Hovered.", "Pinned."], notifications.Toasts.Select(t => t.Message));

        // A question has no timer. Left by the pointer, the hovered one goes a moment later (after the question's second
        // would have been up); the others stay.
        service.Info("Choose one.", "Yes", "No");
        await ShellFixture.PumpUntil(() => notifications.Toasts.Count == 3);
        notifications.Toasts[0].IsHovered = false;
        await ShellFixture.PumpUntil(() => notifications.Toasts.Count == 2, seconds: 10);
        Assert.Equal(["Pinned.", "Choose one."], notifications.Toasts.Select(t => t.Message));
        Assert.Equal(4, notifications.Count); // all still in the Notifications window
    }
}
