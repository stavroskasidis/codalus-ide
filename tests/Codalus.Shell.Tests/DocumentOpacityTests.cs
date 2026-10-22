using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace Codalus.Shell.Tests;

/// <summary>
/// #83: the classic theme's colors read 15% dimmer than the classic IDE's. Dock's deferred content faded a switched tab's
/// view in from Opacity 0.85, and every other switch left the whole document view there. The shell turns the fade off.
/// </summary>
public sealed class DocumentOpacityTests
{
    [AvaloniaFact]
    public async Task Switching_documents_leaves_the_editor_fully_opaque()
    {
        await using var f = ShellFixture.Create();
        var folder = Path.Combine(Path.GetTempPath(), "codalus-opacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            List<string> paths = [Path.Combine(folder, "A.cs"), Path.Combine(folder, "B.cs")];
            foreach (var path in paths)
            {
                await File.WriteAllTextAsync(path, "class " + Path.GetFileNameWithoutExtension(path) + " { }", TestContext.Current.CancellationToken);
                await f.Shell.OpenFileAsync(path, preview: false, null);
                ShellFixture.Pump();
            }

            // Five switches: the fade left 0.85 behind on every other one.
            for (var i = 0; i < 5; i++)
            {
                var document = await f.Shell.OpenFileAsync(paths[i % 2], preview: false, null);
                await ShellFixture.PumpUntil(() => document?.Controller is not null);
                await Task.Delay(300, TestContext.Current.CancellationToken); // longer than the fade's 90 ms
                ShellFixture.Pump();
                var editor = document!.Controller!.Editor;
                var dimmed = editor.GetSelfAndVisualAncestors().OfType<Visual>().Where(v => v.Opacity < 1).Select(v => $"{v.GetType().Name} {v.Opacity}").ToList();
                Assert.True(dimmed.Count == 0, $"Switch {i + 1}: the editor is drawn under {string.Join(", ", dimmed)}");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
