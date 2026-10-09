using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Daedalus.Diagnostics.BenchEvents;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Shell.Editor;

namespace Daedalus.Shell.Infrastructure;

/// <summary>
/// Benchmark support (<c>--open</c>, <c>--bench typing|scroll</c>, <c>--exit-on highlight|done|language</c>): opens a
/// file, reports when it first shows semantic highlighting, and runs self-driven typing and scrolling with in-app
/// frame probes. These complement the harness's external measurements and are the only source on CI runners.
/// </summary>
internal sealed class BenchDriver(Window window, ShellHost shell)
{
    private const int TypingKeystrokes = 300;
    private const double ScrollPixelsPerSecond = 4000;
    private static readonly TimeSpan s_typingInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan s_scrollDuration = TimeSpan.FromSeconds(10);
    private bool _synthesizing;

    public static bool IsRequested(ShellOptions options) =>
        options.OpenFile is not null || options.Bench is not null || options.ExitOn is "language";

    public async Task RunAsync()
    {
        if (shell.Options.ExitOn == "language")
        {
            shell.Languages.StatusChanged += (_, e) =>
            {
                if (e.Status.State == WorkspaceStates.Ready)
                {
                    Exit();
                }
            };
        }

        if (shell.Options.OpenFile is not { } file)
        {
            return;
        }

        var document = await shell.OpenFileAsync(file).ConfigureAwait(true);
        if (document is null)
        {
            return;
        }

        var controller = await WaitForControllerAsync(document).ConfigureAwait(true);
        var highlighted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.TokensApplied += (_, _) => highlighted.TrySetResult();
        if (controller.Colorizer.HasTokens)
        {
            highlighted.TrySetResult();
        }

        await highlighted.Task.ConfigureAwait(true);
        await RenderedAsync(window).ConfigureAwait(true);
        BenchEvents.Emit(BenchEventNames.HighlightReady);
        if (shell.Options.ExitOn == "highlight")
        {
            Exit();
            return;
        }

        // Real keyboard input is always probed, so external drivers (the harness) also get in-app numbers.
        controller.Editor.TextArea.TextEntering += (_, _) =>
        {
            if (!_synthesizing)
            {
                ProbeInputToRender(Stopwatch.GetTimestamp(), synthetic: false);
            }
        };

        switch (shell.Options.Bench)
        {
            case "typing":
                await TypeAsync(controller).ConfigureAwait(true);
                break;
            case "scroll":
                await ScrollAsync(controller).ConfigureAwait(true);
                break;
            default:
                return;
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        if (shell.Options.ExitOn == "done")
        {
            Exit();
        }
    }

    private static void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown(), DispatcherPriority.Background);
        }
    }

    private static async Task<CodeEditorController> WaitForControllerAsync(CodeEditorDocument document)
    {
        for (var i = 0; i < 1200 && document.Controller is null; i++)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        return document.Controller ?? throw new InvalidOperationException("The editor did not appear.");
    }

    private async Task TypeAsync(CodeEditorController controller)
    {
        var editor = controller.Editor;
        var document = editor.Document;

        // Type inside the middle of the file, so the classifier has real work to do.
        var line = document.GetLineByNumber(Math.Max(1, document.LineCount / 2));
        editor.TextArea.Caret.Offset = line.EndOffset;
        editor.ScrollToLine(line.LineNumber);
        _synthesizing = true;
        editor.TextArea.PerformTextInput("\n// ");
        _synthesizing = false;
        await Task.Delay(500).ConfigureAwait(true);

        const string Sample = "daedalus typing latency probe ";
        for (var i = 0; i < TypingKeystrokes; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            _synthesizing = true;
            editor.TextArea.PerformTextInput(Sample[i % Sample.Length].ToString());
            _synthesizing = false;
            ProbeInputToRender(t0, synthetic: true);
            await Task.Delay(s_typingInterval).ConfigureAwait(true);
        }

        await Task.Delay(1000).ConfigureAwait(true);
    }

    private async Task ScrollAsync(CodeEditorController controller)
    {
        var editor = controller.Editor;
        var compositor = Compositor(window);
        var done = new TaskCompletionSource();
        var start = Stopwatch.GetTimestamp();
        long previousRendered = 0;

        void Frame(TimeSpan _)
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            editor.ScrollToVerticalOffset(elapsed.TotalSeconds * ScrollPixelsPerSecond);
            compositor?.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ =>
            {
                var now = Stopwatch.GetTimestamp();
                var previous = Interlocked.Exchange(ref previousRendered, now);
                if (previous != 0)
                {
                    BenchEvents.Emit(BenchEventNames.ScrollFrame,
                    [
                        ("frame_us", (long)Stopwatch.GetElapsedTime(previous, now).TotalMicroseconds),
                        ("gc_pause_us", (long)GC.GetTotalPauseDuration().TotalMicroseconds),
                        ("gen2", GC.CollectionCount(2)),
                    ]);
                }
            }, TaskScheduler.Default);

            if (elapsed < s_scrollDuration)
            {
                window.RequestAnimationFrame(Frame);
            }
            else
            {
                done.SetResult();
            }
        }

        window.RequestAnimationFrame(Frame);
        await done.Task.ConfigureAwait(true);

        // Large jumps (go-to-line style): time from the jump to the frame showing it.
        foreach (var fraction in new[] { 0.25, 0.5, 0.75, 1.0, 0.0 })
        {
            var t0 = Stopwatch.GetTimestamp();
            editor.ScrollToLine(Math.Max(1, (int)(editor.Document.LineCount * fraction)));
            await RenderedAsync(window).ConfigureAwait(true);
            BenchEvents.Emit("jump_frame", "frame_us", (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            await Task.Delay(200).ConfigureAwait(true);
        }
    }

    /// <summary>Emits the time from <paramref name="t0"/> until the next composition batch has been rendered.</summary>
    private void ProbeInputToRender(long t0, bool synthetic)
    {
        window.RequestAnimationFrame(_ =>
        {
            if (Compositor(window) is not { } compositor)
            {
                return;
            }

            compositor.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ =>
                BenchEvents.Emit(BenchEventNames.KeyLatency,
                [
                    ("latency_us", (long)Stopwatch.GetElapsedTime(t0).TotalMicroseconds),
                    ("synthetic", synthetic ? 1 : 0),
                ]), TaskScheduler.Default);
        });
    }

    private static Task RenderedAsync(Window window)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.RequestAnimationFrame(_ =>
        {
            if (Compositor(window) is { } compositor)
            {
                compositor.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default);
            }
            else
            {
                tcs.TrySetResult();
            }
        });
        return tcs.Task;
    }

    private static Compositor? Compositor(Window window) => ElementComposition.GetElementVisual(window)?.Compositor;
}
