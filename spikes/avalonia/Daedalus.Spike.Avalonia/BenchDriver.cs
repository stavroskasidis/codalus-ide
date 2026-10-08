using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Daedalus.Diagnostics.BenchEvents;

namespace Daedalus.Spike.Avalonia;

/// <summary>
/// Self-driven benchmark scenarios (<c>--bench</c>) and in-app frame probes. These complement the harness's
/// external measurements (PresentMon/SendInput on Windows) and are the only source on CI runners.
/// </summary>
internal sealed class BenchDriver(Window window, ShellViewModel shell)
{
    private const int TypingKeystrokes = 300;
    private static readonly TimeSpan s_typingInterval = TimeSpan.FromMilliseconds(40);
    private const double ScrollPixelsPerSecond = 4000;
    private static readonly TimeSpan s_scrollDuration = TimeSpan.FromSeconds(10);
    private bool _synthesizing;

    public void ExitIfRequested(string eventName)
    {
        var exitOn = shell.Options.ExitOn;
        var matches = exitOn switch
        {
            "interactive" => eventName == BenchEventNames.Interactive,
            "highlight" => eventName == BenchEventNames.HighlightReady,
            "done" => eventName == BenchEventNames.ScenarioDone,
            _ => false,
        };
        if (matches && global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.Post(() => desktop.Shutdown(), DispatcherPriority.Background);
        }
    }

    public async Task RunAsync()
    {
        if (shell.Options.OpenFile is null)
        {
            if (shell.Options.Screenshot is { } shot)
            {
                await Task.Delay(500).ConfigureAwait(true);
                SaveScreenshot(shot);
                File.WriteAllLines(shot + ".tree.txt", Dump(window, 0));
            }

            return;
        }

        var editor = await WaitForEditorAsync().ConfigureAwait(true);
        await editor.Document.Controller.FirstHighlight.ConfigureAwait(true);
        if (shell.Options.Screenshot is { } screenshot)
        {
            await Task.Delay(500).ConfigureAwait(true);
            SaveScreenshot(screenshot);
        }

        ExitIfRequested(BenchEventNames.HighlightReady);

        // Real keyboard input is always probed, so external drivers (the harness) also get in-app numbers.
        editor.Editor.TextArea.TextEntering += (_, _) =>
        {
            if (!_synthesizing)
            {
                ProbeInputToRender(Stopwatch.GetTimestamp(), synthetic: false);
            }
        };

        switch (shell.Options.Bench)
        {
            case "typing":
                await TypeAsync(editor).ConfigureAwait(true);
                break;
            case "scroll":
                await ScrollAsync(editor).ConfigureAwait(true);
                break;
            default:
                return;
        }

        BenchEvents.Emit(BenchEventNames.ScenarioDone);
        ExitIfRequested(BenchEventNames.ScenarioDone);
    }

    private async Task<EditorView> WaitForEditorAsync()
    {
        for (var i = 0; i < 600; i++)
        {
            if (window.GetVisualDescendants().OfType<EditorView>().FirstOrDefault() is { } view)
            {
                return view;
            }

            await Task.Delay(50).ConfigureAwait(true);
        }

        throw new InvalidOperationException("Editor did not appear.");
    }

    private async Task TypeAsync(EditorView view)
    {
        var editor = view.Editor;
        var document = editor.Document;

        // Type inside the body of the middle method of the file so the classifier has real work to do.
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

    private async Task ScrollAsync(EditorView view)
    {
        var editor = view.Editor;
        var compositor = Compositor(window);
        var done = new TaskCompletionSource();
        var start = Stopwatch.GetTimestamp();
        long previousRendered = 0;

        void Frame(TimeSpan _)
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            editor.ScrollToVerticalOffset(elapsed.TotalSeconds * ScrollPixelsPerSecond);
            if (compositor is not null)
            {
                compositor.RequestCompositionBatchCommitAsync().Rendered.ContinueWith(_ =>
                {
                    var now = Stopwatch.GetTimestamp();
                    var previous = Interlocked.Exchange(ref previousRendered, now);
                    if (previous != 0)
                    {
                        BenchEvents.Emit(BenchEventNames.ScrollFrame, "frame_us", (long)Stopwatch.GetElapsedTime(previous, now).TotalMicroseconds);
                    }
                }, TaskScheduler.Default);
            }

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

    private static IEnumerable<string> Dump(global::Avalonia.Visual visual, int depth)
    {
        yield return new string(' ', depth * 2) + visual.GetType().Name + " " + visual.Bounds + " dc=" + (visual as global::Avalonia.StyledElement)?.DataContext?.GetType().Name;
        foreach (var child in visual.GetVisualChildren())
        {
            foreach (var line in Dump(child, depth + 1))
            {
                yield return line;
            }
        }
    }

    private void SaveScreenshot(string path)
    {
        var scaling = window.RenderScaling;
        var size = new global::Avalonia.PixelSize((int)(window.Bounds.Width * scaling), (int)(window.Bounds.Height * scaling));
        using var bitmap = new global::Avalonia.Media.Imaging.RenderTargetBitmap(size, new global::Avalonia.Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);
        bitmap.Save(path, global::Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static Compositor? Compositor(Window window) => ElementComposition.GetElementVisual(window)?.Compositor;
}
