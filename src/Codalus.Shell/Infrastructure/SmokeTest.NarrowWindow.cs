using Avalonia.Controls;
using Avalonia.VisualTree;
using Codalus.Shell.Views;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// 0.6.0: the real window made narrow (the maintainer's report: the menu ran under the caption buttons on Windows and the
/// toolbar wrapped). At each width the title row keeps the caption buttons' room and its drag area clear, the menu shows
/// every item (on two rows at 650 px, the search box at the end of the second), and the toolbar stays one row, its groups
/// folded into "…" drop-downs (at 650 px, with the solution's configuration and startup project on it). Photographed as
/// <c>smoke-test-narrow-{width}.png</c>.
/// </summary>
internal sealed partial class SmokeTest
{
    private static readonly double[] s_narrowWidths = [1000, 800, 650];

    private async Task NarrowWindowStepAsync()
    {
        if (window is not MainWindow main)
        {
            throw new SmokeFailure("the smoke test's window is not the main window");
        }

        var (state, width, height) = (main.WindowState, main.Width, main.Height);
        try
        {
            main.WindowState = WindowState.Normal;
            foreach (var narrow in s_narrowWidths)
            {
                main.Width = narrow;
                await WaitAsync(() => Math.Abs(main.Bounds.Width - narrow) < 2, $"the window is {narrow} px wide", 15).ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);
                foreach (var problem in main.TitleAndToolbarProblems())
                {
                    Check(false, $"{narrow} px: {problem}");
                }

                if (narrow <= 650 && main.MenuBar.IsVisible)
                {
                    Check(main.TitleRow.IsWrapped, "at 650 px the menu wraps onto a second row");
                }

                if (narrow <= 650 && shell.Toolbar.HasSolution)
                {
                    var toolbar = main.GetVisualDescendants().OfType<StandardToolbar>().First();
                    Check(toolbar.Panel.Folded.Count > 0 && toolbar.Panel.Overflows.Any(), $"at {narrow} px the toolbar folds groups into \"…\" drop-downs");
                }

                Screenshot($"smoke-test-narrow-{narrow}.png");
            }
        }
        finally
        {
            main.Width = width;
            main.Height = height;
            main.WindowState = state;
            await RenderedAsync().ConfigureAwait(true);
        }
    }
}
