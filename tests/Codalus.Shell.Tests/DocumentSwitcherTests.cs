using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Codalus.Shell.Tests;

/// <summary>
/// Deferred work (M12b): the Ctrl+Tab switcher. Holding Ctrl, each Tab moves through the open documents in the order
/// they were last used; releasing Ctrl switches; Esc doesn't.
/// </summary>
public sealed class DocumentSwitcherTests
{
    [AvaloniaFact]
    public async Task Ctrl_tab_switches_to_the_documents_used_before_in_their_order()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(Path.GetTempPath(), "codalus-switcher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            List<string> paths = [Path.Combine(folder, "A.txt"), Path.Combine(folder, "B.txt"), Path.Combine(folder, "C.txt")];
            foreach (var path in paths)
            {
                await File.WriteAllTextAsync(path, Path.GetFileName(path), TestContext.Current.CancellationToken);
                await f.Shell.OpenFileAsync(path, preview: false, null);
                ShellFixture.Pump();
            }

            // Used last: C, then B, then A. Reactivate A: the order is A, C, B.
            await f.Shell.OpenFileAsync(paths[0], preview: false, null);
            ShellFixture.Pump();
            Assert.Equal(["A.txt", "C.txt", "B.txt"], f.Shell.RecentDocuments.Select(d => d.Title));

            // Ctrl+Tab, then release: the one used before (C).
            f.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            ShellFixture.Pump();
            Assert.True(f.Shell.Switcher.IsOpen);
            Assert.Equal("C.txt", f.Shell.Switcher.Selected?.Title);
            f.Window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
            ShellFixture.Pump();
            Assert.False(f.Shell.Switcher.IsOpen);
            Assert.Equal("C.txt", f.Shell.Layout.ActiveDocument?.Title);

            // Ctrl held, Tab twice: two back (B); Shift+Tab one forward again (A); Esc stays on C.
            f.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            f.Window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            ShellFixture.Pump();
            Assert.Equal("B.txt", f.Shell.Switcher.Selected?.Title);
            f.Window.KeyPress(Key.Tab, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Tab, null);
            ShellFixture.Pump();
            Assert.Equal("A.txt", f.Shell.Switcher.Selected?.Title);
            f.Window.KeyPress(Key.Escape, RawInputModifiers.Control, PhysicalKey.Escape, null);
            ShellFixture.Pump();
            Assert.False(f.Shell.Switcher.IsOpen);
            Assert.Equal("C.txt", f.Shell.Layout.ActiveDocument?.Title);

            // Closed documents leave the order.
            f.Shell.Layout.CloseDocument(f.Shell.Layout.Documents.Single(d => d.Title == "A.txt"));
            ShellFixture.Pump();
            Assert.Equal(["C.txt", "B.txt"], f.Shell.RecentDocuments.Select(d => d.Title));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
