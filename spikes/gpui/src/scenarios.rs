//! Self-driven benchmark scenarios, mirroring prototype A's `BenchDriver`.

use std::time::Duration;

use futures::channel::oneshot;
use gpui_kit::component::input::RopeExt as _;
use gpui_kit::{AsyncWindowContext, WeakEntity};

use crate::bench;
use crate::workspace::{EditorPanel, probe_input_to_present, set_cursor_line};

const TYPING_KEYSTROKES: usize = 300;
const TYPING_INTERVAL: Duration = Duration::from_millis(40);
pub const SCROLL_LINES_PER_SECOND: f64 = 230.0;
const SCROLL_DURATION: Duration = Duration::from_secs(10);

pub async fn typing(panel: WeakEntity<EditorPanel>, cx: &mut AsyncWindowContext) {
    // Type inside the middle of the file so the classifier has real work to do.
    let _ = panel.update_in(cx, |panel, window, cx| {
        let state = panel.state().clone();
        let middle = state.read(cx).text().lines_len() / 2;
        set_cursor_line(&state, middle, window, cx);
        state.update(cx, |state, cx| state.insert("// \n", window, cx));
        state.update(cx, |state, cx| {
            state.set_cursor_position(gpui_kit::component::input::Position::new(middle as u32, 3), window, cx)
        });
    });
    cx.background_executor().timer(Duration::from_millis(500)).await;

    let sample: Vec<char> = "daedalus typing latency probe ".chars().collect();
    for i in 0..TYPING_KEYSTROKES {
        let ch = sample[i % sample.len()].to_string();
        let _ = panel.update_in(cx, |panel, window, cx| {
            let t0 = bench::mono_us();
            panel.state().update(cx, |state, cx| state.insert(ch, window, cx));
            probe_input_to_present(t0, true, window);
        });
        cx.background_executor().timer(TYPING_INTERVAL).await;
    }
    cx.background_executor().timer(Duration::from_secs(1)).await;
}

pub async fn scroll(panel: WeakEntity<EditorPanel>, cx: &mut AsyncWindowContext) {
    let line_count = panel
        .update(cx, |panel, cx| panel.state().read(cx).text().lines_len())
        .unwrap_or(1);
    let Ok(state) = panel.update(cx, |panel, _| panel.state().clone()) else { return };

    // A synchronous next-frame chain (GPUI's requestAnimationFrame), like prototype A's loop:
    // every frame records its interval and advances the viewport for the following frame.
    let (done_tx, done_rx) = oneshot::channel::<()>();
    let start = bench::mono_us();
    let _ = cx.update(|window, _| {
        scroll_frame(window, state, line_count, start, None, done_tx);
    });
    let _ = done_rx.await;

    // Large jumps (go-to-line style): time from the jump to the frame showing it.
    for fraction in [0.25, 0.5, 0.75, 1.0, 0.0] {
        let t0 = bench::mono_us();
        let line = ((line_count - 1) as f64 * fraction) as usize;
        let _ = panel.update_in(cx, |panel, window, cx| set_cursor_line(&panel.state().clone(), line, window, cx));
        next_frame(cx).await;
        // Yield once more so the frame has been presented.
        cx.background_executor().timer(Duration::from_millis(0)).await;
        bench::emit("jump_frame", &[("frame_us", bench::mono_us() - t0)]);
        cx.background_executor().timer(Duration::from_millis(200)).await;
    }
}

fn scroll_frame(
    window: &mut gpui_kit::Window,
    state: gpui_kit::Entity<gpui_kit::component::input::EditorState>,
    line_count: usize,
    start: i64,
    previous: Option<i64>,
    done: oneshot::Sender<()>,
) {
    window.on_next_frame(move |window, cx| {
        let now = bench::mono_us();
        if let Some(previous) = previous {
            bench::emit(bench::SCROLL_FRAME, &[("frame_us", now - previous)]);
        }
        let elapsed = Duration::from_micros((now - start).max(0) as u64);
        if elapsed >= SCROLL_DURATION {
            let _ = done.send(());
            return;
        }
        // Keep the cursor at the bottom of a viewport that advances at a fixed line rate.
        let line = ((elapsed.as_secs_f64() * SCROLL_LINES_PER_SECOND) as usize + 40).min(line_count - 1);
        set_cursor_line(&state, line, window, cx);
        scroll_frame(window, state, line_count, start, Some(now), done);
    });
}

/// Resolves at the start of the next frame with its monotonic timestamp.
async fn next_frame(cx: &mut AsyncWindowContext) -> i64 {
    let (tx, rx) = oneshot::channel();
    cx.on_next_frame(move |window, _| {
        window.refresh();
        let _ = tx.send(bench::mono_us());
    });
    rx.await.unwrap_or_else(|_| bench::mono_us())
}
