using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// The dialog queue: a dialog asked for off the UI thread is set up and shown on it, and one that fails doesn't fail the
/// dialogs after it. PR #577's first run: the remote debugger's consent to copy it was asked from its service's thread,
/// laying the dialog out there threw, and each of the smoke test's 40 dialogs after it threw the same. Its second: with
/// only the layout moved to the UI thread, the buttons changed on the asking thread were laid out twice.
/// </summary>
public sealed class DialogQueueTests
{
    [AvaloniaFact]
    public async Task A_dialog_asked_for_off_the_UI_thread_opens_with_its_buttons_laid_out()
    {
        await using var f = ShellFixture.Create();
        var request = new DialogRequest("Copy the Debugger", "The debugger isn't on the target. Copy it there?", ["Copy", "Cancel"]);

        // A dialog shown and closed on the UI thread first, as in the smoke run: the frame laid out once.
        var first = f.Shell.Dialogs.ShowAsync(new DialogRequest("Rename", "New name:", ["OK", "Cancel"]) { Input = "x" });
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        ShellFixture.Pump();
        f.Shell.Dialogs.Cancel();
        await first;
        ShellFixture.Pump();

        // Every change the view sees happens on the UI thread: changed from the asking thread, the buttons were laid out twice
        // (PR #577's CI, run 46090778794: Copy, Cancel, Copy, Cancel).
        var offThread = new List<string>();
        void Check(string what)
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                lock (offThread)
                {
                    offThread.Add(what);
                }
            }
        }

        f.Shell.Dialogs.Buttons.CollectionChanged += (_, e) => Check($"Buttons {e.Action}");
        f.Shell.Dialogs.Opened += (_, _) => Check("Opened");
        var shown = await Task.Factory.StartNew(() => f.Shell.Dialogs.ShowAsync(request), TestContext.Current.CancellationToken, TaskCreationOptions.None, TaskScheduler.Default);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen, describe: () => $"the dialog: {(shown.IsFaulted ? shown.Exception!.InnerException!.ToString() : "not open")}");
        ShellFixture.Pump();
        ShellFixture.Pump();
        Assert.False(shown.IsCompleted, $"the dialog's task ended as it opened: {shown.Exception?.InnerException}");
        Assert.Empty(offThread);

        var view = f.Window.GetVisualDescendants().OfType<DialogView>().Single();
        var footer = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("dialogFooter"));
        var buttons = footer.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("dialog")).ToList();
        Assert.Equal(f.Shell.Dialogs.Buttons.Select(b => b.Label), buttons.Select(b => b.Content as string));
        Assert.All(buttons, b => Assert.True(b.Bounds.Width >= b.DesiredSize.Width - 0.01 && b.TranslatePoint(new Point(b.Bounds.Width, 0), footer)!.Value.X <= footer.Bounds.Width - footer.Padding.Right + 0.01,
            $"{b.Content} at {b.TranslatePoint(default, footer)}, {b.Bounds.Width} px wide, in a footer {footer.Bounds.Width} px wide"));

        f.Shell.Dialogs.Accept();
        Assert.Equal("Copy", (await shown).Button);
    }

    [AvaloniaFact]
    public async Task A_dialog_that_fails_to_open_doesnt_fail_the_ones_after_it()
    {
        var dialog = new DialogViewModel();
        var failing = dialog.ShowAsync(new DialogRequest("Rename", "New name:", ["OK", "Cancel"]) { Input = "x", Validate = _ => throw new InvalidOperationException("the request's own failure") });
        Assert.Equal("the request's own failure", (await Assert.ThrowsAsync<InvalidOperationException>(() => failing)).Message);
        Assert.False(dialog.IsOpen); // not left open half shown

        var next = dialog.ShowAsync(new DialogRequest("Delete feature/sign-in?", "The branch is deleted.", ["Delete", "Cancel"]) { DefaultButton = "Cancel" });
        ShellFixture.Pump();
        Assert.True(dialog.IsOpen);
        Assert.Equal("Delete feature/sign-in?", dialog.Title);
        Assert.Equal(["Delete", "Cancel"], dialog.Buttons.Select(b => b.Label));
        dialog.Accept();
        Assert.Equal("Cancel", (await next).Button);
    }
}
