//! The spike's main view: a dock area with Solution Explorer (left), editors (center) and
//! Output (bottom), plus a status bar. Owns the supervised language-service host.

use std::cell::Cell;
use std::rc::Rc;
use std::sync::{Arc, mpsc};
use std::time::Duration;

use gpui_kit::component::dock::{DockArea, DockLayout, DockPlacement, DockSkin, Panel, PanelEvent, panel_handle};
use gpui_kit::component::input::{Editor, EditorState, InputEvent, Position};
use gpui_kit::component::list::ListItem;
use gpui_kit::component::tree::{TreeItem, TreeState, tree};
use gpui_kit::component::{ActiveTheme, h_flex, v_flex};
use gpui_kit::*;
use serde_json::{Value, json};

use crate::Options;
use crate::bench;
use crate::doc_sync::{DocSync, WindowedTokensProvider};
use crate::rpc::{HostClient, HostEvent, HostLaunch};

const MAX_OUTPUT_LINES: usize = 500;

pub struct Workspace {
    dock_area: Entity<DockArea>,
    explorer: Entity<ExplorerPanel>,
    output: Entity<OutputPanel>,
    editors: Vec<Entity<EditorPanel>>,
    host: Option<Arc<HostClient>>,
    status: SharedString,
    options: Options,
    restarts: Vec<std::time::Instant>,
    _tasks: Vec<Task<()>>,
    _subscriptions: Vec<Subscription>,
}

impl Workspace {
    pub fn new(options: Options, window: &mut Window, cx: &mut Context<Self>) -> Self {
        let (dock_area, _skin) = DockSkin::dock_area("daedalus", Some(1), window, cx);
        let explorer = cx.new(ExplorerPanel::new);
        let output = cx.new(|cx| OutputPanel {
            lines: Vec::new(),
            focus: cx.focus_handle(),
        });
        let open_subscription = cx.subscribe_in(&explorer, window, |this, _, event: &OpenDocument, window, cx| {
            if !this.editors.iter().any(|e| e.read(cx).path == event.0) {
                this.open_document(event.0.clone(), window, cx);
            }
        });

        dock_area.update(cx, |area, cx| {
            area.set_center(DockLayout::v_split().child(DockLayout::tabs(), None), window, cx);
            area.set_dock(
                DockPlacement::Left,
                DockLayout::v_split().child(DockLayout::tabs().panel_view(panel_handle(explorer.clone()), cx), None),
                window,
                cx,
            );
            area.set_dock_size(DockPlacement::Left, px(280.), window, cx);
            area.set_dock(
                DockPlacement::Bottom,
                DockLayout::v_split().child(DockLayout::tabs().panel_view(panel_handle(output.clone()), cx), None),
                window,
                cx,
            );
            area.set_dock_size(DockPlacement::Bottom, px(180.), window, cx);
        });

        // first_frame: the second next-frame callback proves the first frame was produced.
        // interactive: a foreground task after that, i.e. once the UI thread is idle.
        window.on_next_frame(|window, _| {
            window.on_next_frame(|_, cx| {
                bench::emit(bench::FIRST_FRAME, &[]);
                cx.spawn(async move |_| bench::emit(bench::INTERACTIVE, &[])).detach();
            });
        });

        let mut this = Self {
            dock_area,
            explorer,
            output,
            editors: Vec::new(),
            host: None,
            status: "Starting language service…".into(),
            options,
            restarts: Vec::new(),
            _tasks: Vec::new(),
            _subscriptions: vec![open_subscription],
        };
        this.exit_if_requested(bench::INTERACTIVE, cx);
        this.start_host(window, cx);
        this
    }

    fn exit_if_requested(&self, event: &'static str, cx: &mut Context<Self>) {
        let matches = match self.options.exit_on.as_deref() {
            Some("interactive") => event == bench::INTERACTIVE,
            Some("highlight") => event == bench::HIGHLIGHT_READY,
            Some("done") => event == bench::SCENARIO_DONE,
            _ => false,
        };
        if matches && event == bench::INTERACTIVE {
            // Wait for the interactive event itself (emitted from a frame callback).
            cx.spawn(async move |_, cx| {
                cx.background_executor().timer(Duration::from_millis(50)).await;
                cx.update(|cx| cx.quit());
            })
            .detach();
        } else if matches {
            cx.quit();
        }
    }

    fn log(&mut self, line: impl Into<String>, cx: &mut Context<Self>) {
        let line = line.into();
        self.output.update(cx, |output, cx| {
            output.lines.push(line.into());
            if output.lines.len() > MAX_OUTPUT_LINES {
                output.lines.remove(0);
            }
            cx.notify();
        });
    }

    fn set_status(&mut self, status: impl Into<SharedString>, cx: &mut Context<Self>) {
        self.status = status.into();
        cx.notify();
    }

    fn start_host(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(launch) = HostLaunch::resolve() else {
            self.set_status("Language service host not found (set DAEDALUS_LSHOST).", cx);
            return;
        };
        let (events_tx, events_rx) = mpsc::channel();
        let host = HostClient::new(launch, events_tx);
        if let Err(error) = host.restart() {
            self.set_status(format!("Failed to start language service: {error}"), cx);
            return;
        }
        self.host = Some(host.clone());

        // Pump host events (crashes, stderr) onto the UI thread.
        let events_rx = Arc::new(std::sync::Mutex::new(events_rx));
        self._tasks.push(cx.spawn_in(window, async move |this, cx| {
            loop {
                let rx = events_rx.clone();
                let event = cx
                    .background_executor()
                    .spawn(async move { rx.lock().unwrap().recv().ok() })
                    .await;
                let Some(event) = event else { break };
                if this
                    .update_in(cx, |this, window, cx| this.on_host_event(event, window, cx))
                    .is_err()
                {
                    break;
                }
            }
        }));

        self.initialize_host(host, window, cx);
    }

    fn initialize_host(&mut self, host: Arc<HostClient>, window: &mut Window, cx: &mut Context<Self>) {
        let solution = self.options.solution.clone();
        let open = self.options.open.clone();
        let restarting = !self.editors.is_empty();
        let task = cx.spawn_in(window, async move |this, cx| {
            let init = host
                .request(
                    "initialize",
                    json!({ "processId": std::process::id(), "clientName": "daedalus-gpui" }),
                )
                .await;
            if let Err(error) = init {
                let _ = this.update(cx, |this, cx| {
                    this.set_status(format!("Language service failed: {error}"), cx)
                });
                return;
            }
            let _ = this.update(cx, |this, cx| this.set_status("Language service ready", cx));

            if let Some(solution) = solution {
                let _ = this.update(cx, |this, cx| this.set_status(format!("Loading {solution}…"), cx));
                match host.request("daedalus/openSolution", json!({ "path": solution })).await {
                    Ok(result) => {
                        let _ = this.update(cx, |this, cx| {
                            if let Some(root) = result.get("root") {
                                this.explorer.update(cx, |explorer, cx| explorer.set_root(root, cx));
                            }
                            for diagnostic in result
                                .get("diagnostics")
                                .and_then(Value::as_array)
                                .into_iter()
                                .flatten()
                            {
                                this.log(format!("[workspace] {}", diagnostic.as_str().unwrap_or_default()), cx);
                            }
                            bench::emit(bench::SOLUTION_LOADED, &[]);
                            this.set_status("Ready", cx);
                        });
                    }
                    Err(error) => {
                        let _ = this.update(cx, |this, cx| this.log(format!("[error] {error}"), cx));
                    }
                }
            }

            if restarting {
                // Documents re-open themselves lazily on the new generation; trigger a fetch.
                let _ = this.update_in(cx, |this, window, cx| {
                    for editor in this.editors.clone() {
                        editor.update(cx, |editor, cx| editor.refresh_tokens(window, cx));
                    }
                });
            } else if let Some(path) = open {
                let _ = this.update_in(cx, |this, window, cx| this.open_document(path, window, cx));
            }
        });
        self._tasks.push(task);
    }

    fn on_host_event(&mut self, event: HostEvent, window: &mut Window, cx: &mut Context<Self>) {
        match event {
            HostEvent::Log(line) => self.log(line, cx),
            HostEvent::Crashed { generation, exit_code } => {
                let Some(host) = self.host.clone() else { return };
                if generation != host.generation() {
                    return; // A previous generation that was replaced deliberately.
                }
                let now = std::time::Instant::now();
                self.restarts
                    .retain(|t| now.duration_since(*t) < Duration::from_secs(60));
                if self.restarts.len() >= 5 {
                    self.set_status("Language service crashed repeatedly and was stopped.", cx);
                    self.log(
                        "[notification] Language service crashed repeatedly and was stopped.",
                        cx,
                    );
                    return;
                }
                self.restarts.push(now);
                self.set_status("Language service crashed — restarting…", cx);
                self.log(
                    format!("[notification] Language service crashed (exit code {exit_code:?}); restarting."),
                    cx,
                );
                let delay = Duration::from_millis(250 << (self.restarts.len() - 1).min(4));
                let task = cx.spawn_in(window, async move |this, cx| {
                    cx.background_executor().timer(delay).await;
                    let _ = this.update_in(cx, |this, window, cx| match host.restart() {
                        Ok(_) => {
                            this.log("[notification] Language service restarted; state restored.", cx);
                            this.initialize_host(host.clone(), window, cx);
                        }
                        Err(error) => this.set_status(format!("Language service restart failed: {error}"), cx),
                    });
                });
                self._tasks.push(task);
            }
        }
    }

    pub fn open_document(&mut self, path: String, window: &mut Window, cx: &mut Context<Self>) {
        let Some(host) = self.host.clone() else { return };
        let Ok(text) = std::fs::read_to_string(&path) else {
            self.log(format!("[error] cannot read {path}"), cx);
            return;
        };
        let workspace = cx.entity().downgrade();
        let options = self.options.clone();
        let editor = cx.new(|cx| EditorPanel::new(host, path, text, workspace, options, window, cx));
        self.editors.push(editor.clone());
        self.dock_area.update(cx, |area, cx| {
            area.add_panel_view(panel_handle(editor.clone()), DockPlacement::Center, None, window, cx);
        });
        editor.update(cx, |editor, cx| editor.focus(window, cx));
    }

    fn on_first_highlight(&mut self, cx: &mut Context<Self>) {
        bench::emit(bench::HIGHLIGHT_READY, &[]);
        self.exit_if_requested(bench::HIGHLIGHT_READY, cx);
    }

    fn on_scenario_done(&mut self, cx: &mut Context<Self>) {
        bench::emit(bench::SCENARIO_DONE, &[]);
        self.exit_if_requested(bench::SCENARIO_DONE, cx);
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        if let Some(host) = &self.host {
            host.shutdown();
        }
    }
}

impl Render for Workspace {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .size_full()
            .bg(cx.theme().background)
            .child(div().flex_1().min_h_0().child(self.dock_area.clone()))
            .child(
                h_flex()
                    .px_2()
                    .h(px(22.))
                    .bg(rgb(0x007ACC))
                    .text_color(white())
                    .text_xs()
                    .child(self.status.clone()),
            )
    }
}

// ---------------------------------------------------------------- Solution Explorer

pub struct ExplorerPanel {
    tree: Entity<TreeState>,
    focus: FocusHandle,
}

impl ExplorerPanel {
    fn new(cx: &mut Context<Self>) -> Self {
        Self {
            tree: cx.new(|cx| TreeState::new(cx)),
            focus: cx.focus_handle(),
        }
    }

    fn set_root(&mut self, root: &Value, cx: &mut Context<Self>) {
        let item = to_tree_item(root, 0);
        self.tree.update(cx, |tree, cx| tree.set_items(vec![item], cx));
        cx.notify();
    }
}

fn to_tree_item(node: &Value, depth: usize) -> TreeItem {
    let name = node.get("name").and_then(Value::as_str).unwrap_or("?").to_string();
    let kind = node
        .get("kind")
        .and_then(Value::as_str)
        .unwrap_or("document")
        .to_ascii_lowercase();
    let id = node
        .get("path")
        .and_then(Value::as_str)
        .map(str::to_string)
        .unwrap_or_else(|| format!("{depth}:{name}:{}", uuid_like(&name)));
    let children: Vec<TreeItem> = node
        .get("children")
        .and_then(Value::as_array)
        .map(|c| c.iter().map(|child| to_tree_item(child, depth + 1)).collect())
        .unwrap_or_default();
    let item = TreeItem::new(id, name);
    if kind == "document" {
        item
    } else {
        item.children(children)
            .expanded(kind == "solution" || kind == "project")
    }
}

fn uuid_like(name: &str) -> u64 {
    name.bytes().fold(1469598103934665603u64, |h, b| {
        (h ^ b as u64).wrapping_mul(1099511628211)
    })
}

impl EventEmitter<PanelEvent> for ExplorerPanel {}
impl Focusable for ExplorerPanel {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl gpui_kit::base::dock::Panel for ExplorerPanel {
    fn panel_name(&self) -> &'static str {
        "SolutionExplorer"
    }
}
impl Panel for ExplorerPanel {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        "Solution Explorer"
    }
}

impl Render for ExplorerPanel {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let view = cx.entity();
        div()
            .size_full()
            .child(tree(&self.tree, move |ix, entry, _selected, _window, _cx| {
                let item = entry.item().clone();
                let glyph = if entry.is_folder() {
                    if entry.is_expanded() { "▾" } else { "▸" }
                } else {
                    "·"
                };
                let view = view.clone();
                ListItem::new(ix)
                    .w_full()
                    .py_0p5()
                    .px_2()
                    .pl(px(14.) * entry.depth() + px(8.))
                    .child(h_flex().gap_2().child(glyph).child(item.label.clone()))
                    .on_click(move |_, window, cx| {
                        if item.is_folder() {
                            return;
                        }
                        let path = item.id.to_string();
                        view.update(cx, |_, cx| cx.emit(OpenDocument(path)));
                        window.refresh();
                    })
            }))
    }
}

pub struct OpenDocument(pub String);
impl EventEmitter<OpenDocument> for ExplorerPanel {}

// ---------------------------------------------------------------- Output

pub struct OutputPanel {
    lines: Vec<SharedString>,
    focus: FocusHandle,
}

impl EventEmitter<PanelEvent> for OutputPanel {}
impl Focusable for OutputPanel {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl gpui_kit::base::dock::Panel for OutputPanel {
    fn panel_name(&self) -> &'static str {
        "Output"
    }
}
impl Panel for OutputPanel {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        "Output"
    }
}
impl Render for OutputPanel {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        v_flex()
            .id("output")
            .size_full()
            .overflow_y_scroll()
            .p_1()
            .font_family(cx.theme().mono_font_family.clone())
            .text_xs()
            .children(self.lines.iter().cloned())
    }
}

// ---------------------------------------------------------------- Editor

pub struct EditorPanel {
    title: SharedString,
    path: String,
    state: Entity<EditorState>,
    sync: DocSync,
    visible: Rc<Cell<(usize, usize)>>,
    covered: Rc<Cell<(usize, usize)>>,
    workspace: WeakEntity<Workspace>,
    focus: FocusHandle,
    _subscriptions: Vec<Subscription>,
}

impl EditorPanel {
    fn new(
        host: Arc<HostClient>,
        path: String,
        text: String,
        workspace: WeakEntity<Workspace>,
        options: Options,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        let title: SharedString = std::path::Path::new(&path)
            .file_name()
            .map(|n| n.to_string_lossy().into_owned())
            .unwrap_or_default()
            .into();
        let sync = DocSync::open(host, path.clone(), text.clone());
        let visible = Rc::new(Cell::new((0, 80)));
        let covered = Rc::new(Cell::new((0, 0)));
        let plain = std::env::var("DAEDALUS_SPIKE_PLAIN").is_ok();
        let state = cx.new(|cx| {
            let state = EditorState::new(window, cx);
            let state = if plain { state } else { state.language("csharp") };
            state.line_number(true).soft_wrap(false).default_value(text)
        });

        let received = Rc::new(Cell::new(false));
        let provider = WindowedTokensProvider {
            sync: sync.clone(),
            visible: visible.clone(),
            covered: covered.clone(),
            received: received.clone(),
        };
        let semantic = std::env::var("DAEDALUS_SPIKE_NO_SEMANTIC").is_err();
        state.update(cx, |state, cx| {
            if semantic {
                state.lsp_mut().semantic_tokens_provider = Some(Rc::new(provider));
            }
            state.refresh_semantic_tokens(window, cx);
        });

        let mut subscriptions = vec![cx.subscribe(&state, |this, state, event: &InputEvent, cx| {
            if let InputEvent::Change = event {
                this.sync.sync(state.read(cx).text().clone());
            }
        })];
        // Real keyboard input is always probed (in-app input→present).
        subscriptions
            .push(cx.observe_keystrokes(|_, _, window, _| probe_input_to_present(bench::mono_us(), false, window)));

        let mut this = Self {
            title,
            path,
            state,
            sync,
            visible,
            covered,
            workspace,
            focus: cx.focus_handle(),
            _subscriptions: subscriptions,
        };
        this.watch_first_highlight(received, options, window, cx);
        this
    }

    fn focus(&self, window: &mut Window, cx: &mut Context<Self>) {
        self.state.read(cx).focus_handle(cx).focus(window, cx);
    }

    fn refresh_tokens(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.state
            .update(cx, |state, cx| state.refresh_semantic_tokens(window, cx));
    }

    /// Waits until the first token response has been applied, then runs the bench scenario.
    fn watch_first_highlight(
        &mut self,
        received: Rc<Cell<bool>>,
        options: Options,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        cx.spawn_in(window, async move |this, cx| {
            while !received.get() {
                cx.background_executor().timer(Duration::from_millis(5)).await;
            }
            let _ = this.update_in(cx, |this, window, _cx| {
                window.on_next_frame({
                    let workspace = this.workspace.clone();
                    move |_, cx| {
                        let _ = workspace.update(cx, |workspace, cx| workspace.on_first_highlight(cx));
                    }
                });
                window.refresh();
            });
            match options.bench.as_deref() {
                Some("typing") => crate::scenarios::typing(this.clone(), cx).await,
                Some("scroll") => crate::scenarios::scroll(this.clone(), cx).await,
                _ => return,
            }
            let _ = this.update(cx, |this, cx| {
                let _ = this
                    .workspace
                    .update(cx, |workspace, cx| workspace.on_scenario_done(cx));
            });
        })
        .detach();
    }

    pub fn state(&self) -> &Entity<EditorState> {
        &self.state
    }
}

/// In-app input→present probe: a next-frame callback runs at the start of the frame that shows the
/// change; a foreground task spawned from it runs after that frame has been drawn and presented.
pub fn probe_input_to_present(t0_us: i64, synthetic: bool, window: &mut Window) {
    window.on_next_frame(move |_, cx| {
        cx.spawn(async move |_| {
            bench::emit(
                bench::KEY_LATENCY,
                &[
                    ("latency_us", bench::mono_us() - t0_us),
                    ("synthetic", synthetic as i64),
                ],
            );
        })
        .detach();
    });
}

impl Drop for EditorPanel {
    fn drop(&mut self) {
        self.sync.close();
    }
}

impl EventEmitter<PanelEvent> for EditorPanel {}
impl Focusable for EditorPanel {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus.clone()
    }
}
impl gpui_kit::base::dock::Panel for EditorPanel {
    fn panel_name(&self) -> &'static str {
        "Editor"
    }
    fn closable(&self, _: &App) -> bool {
        true
    }
}
impl Panel for EditorPanel {
    fn title(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        self.title.clone()
    }
}

impl Render for EditorPanel {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        // Track the viewport; refetch tokens when it leaves the window the provider last covered.
        if let Some(rows) = self.state.read(cx).visible_row_range() {
            self.visible.set((rows.start, rows.end));
            let (covered_start, covered_end) = self.covered.get();
            if rows.start < covered_start || rows.end > covered_end {
                self.covered.set((rows.start, rows.end)); // Avoid re-triggering until the fetch lands.
                let state = self.state.clone();
                window.defer(cx, move |window, cx| {
                    state.update(cx, |state, cx| state.refresh_semantic_tokens(window, cx));
                });
            }
        }
        Editor::new(&self.state)
            .bordered(false)
            .p_0()
            .h(relative(1.))
            .font_family(cx.theme().mono_font_family.clone())
            .text_size(cx.theme().mono_font_size)
    }
}

pub fn set_cursor_line(state: &Entity<EditorState>, line: usize, window: &mut Window, cx: &mut App) {
    state.update(cx, |state, cx| {
        state.set_cursor_position(Position::new(line as u32, 0), window, cx)
    });
}
