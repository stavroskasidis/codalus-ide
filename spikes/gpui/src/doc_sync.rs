//! Keeps the host's copy of an open document in sync and serves windowed semantic tokens.
//!
//! The GPUI Kit editor reports *that* the text changed, not *what* changed, so a per-document
//! worker thread diffs each snapshot against its mirror (common prefix/suffix) and sends a
//! single incremental `textDocument/didChange` with UTF-16 offsets (the host's string indices).
//! Token requests go through the same worker, so they are always ordered after the edits
//! they depend on. All of this stays off the UI thread.

use std::cell::Cell;
use std::ops::Range;
use std::rc::Rc;
use std::sync::{Arc, mpsc};

use anyhow::{Result, anyhow};
use futures::channel::oneshot;
use gpui_kit::component::input::{DocumentRangeSemanticTokensProvider, Rope, RopeExt as _};
use gpui_kit::{App, Task, Window};
use lsp_types::{SemanticToken, SemanticTokenType, SemanticTokens, SemanticTokensLegend};
use serde_json::{Value, json};

use crate::rpc::HostClient;

/// Lines fetched around the viewport; scrolling outside this window triggers a refetch.
pub const TOKEN_WINDOW_MARGIN: usize = 120;

type TokenReply = oneshot::Sender<std::pin::Pin<Box<dyn Future<Output = Result<Value>> + Send>>>;

enum Job {
    Sync(Rope),
    Tokens {
        text: Rope,
        lines: Range<usize>,
        reply: TokenReply,
    },
    Close,
}

/// Handle to the sync worker of one open document.
#[derive(Clone)]
pub struct DocSync {
    jobs: mpsc::Sender<Job>,
}

impl DocSync {
    pub fn open(host: Arc<HostClient>, path: String, text: String) -> Self {
        let (tx, rx) = mpsc::channel();
        std::thread::Builder::new()
            .name("doc-sync".into())
            .spawn(move || {
                Worker {
                    host,
                    path,
                    mirror: text,
                    version: 1,
                    opened_generation: 0,
                }
                .run(rx)
            })
            .expect("spawn doc-sync worker");
        Self { jobs: tx }
    }

    pub fn sync(&self, text: Rope) {
        let _ = self.jobs.send(Job::Sync(text));
    }

    /// Requests LSP-encoded tokens for `lines` of `text` (zero-based, end-exclusive).
    pub fn tokens(&self, text: Rope, lines: Range<usize>) -> impl Future<Output = Result<Value>> + 'static {
        let (reply, response) = oneshot::channel();
        let sent = self.jobs.send(Job::Tokens { text, lines, reply });
        async move {
            sent.map_err(|_| anyhow!("document sync worker stopped"))?;
            let pending = response.await.map_err(|_| anyhow!("document sync worker stopped"))?;
            pending.await
        }
    }

    pub fn close(&self) {
        let _ = self.jobs.send(Job::Close);
    }
}

struct Worker {
    host: Arc<HostClient>,
    path: String,
    mirror: String,
    version: i64,
    opened_generation: u64,
}

impl Worker {
    fn run(mut self, jobs: mpsc::Receiver<Job>) {
        for job in jobs {
            match job {
                Job::Sync(text) => self.sync(&text),
                Job::Tokens { text, lines, reply } => {
                    self.sync(&text);
                    let request = self.host.request(
                        "textDocument/semanticTokens/range",
                        json!({ "path": self.path, "startLine": lines.start, "endLine": lines.end }),
                    );
                    let _ = reply.send(Box::pin(request));
                }
                Job::Close => {
                    let _ = self.host.notify("textDocument/didClose", json!({ "path": self.path }));
                    return;
                }
            }
        }
    }

    fn sync(&mut self, text: &Rope) {
        // (Re)open lazily for every host generation, so a restarted host gets our current text.
        let generation = self.host.generation();
        if generation != self.opened_generation {
            let opened = self.host.notify(
                "textDocument/didOpen",
                json!({ "path": self.path, "text": self.mirror, "version": self.version }),
            );
            if opened.is_ok() {
                self.opened_generation = generation;
            }
        }

        let new_text = rope_to_string(text);
        let Some(edit) = diff(&self.mirror, &new_text) else {
            return;
        };
        self.version += 1;
        self.mirror = new_text;
        let _ = self.host.notify(
            "textDocument/didChange",
            json!({ "path": self.path, "version": self.version, "changes": [edit] }),
        );
    }
}

fn rope_to_string(rope: &Rope) -> String {
    let mut text = String::with_capacity(rope.len());
    for chunk in rope.chunks() {
        text.push_str(chunk);
    }
    text
}

/// The single edit turning `old` into `new`, as `{offset, length, text}` in UTF-16 code units.
fn diff(old: &str, new: &str) -> Option<Value> {
    if old == new {
        return None;
    }
    let (a, b) = (old.as_bytes(), new.as_bytes());
    let mut prefix = a.iter().zip(b).take_while(|(x, y)| x == y).count();
    while !old.is_char_boundary(prefix) {
        prefix -= 1;
    }
    let max_suffix = a.len().min(b.len()) - prefix;
    let mut suffix = a
        .iter()
        .rev()
        .zip(b.iter().rev())
        .take(max_suffix)
        .take_while(|(x, y)| x == y)
        .count();
    while !old.is_char_boundary(a.len() - suffix) || !new.is_char_boundary(b.len() - suffix) {
        suffix -= 1;
    }
    let offset = old[..prefix].encode_utf16().count();
    let length = old[prefix..a.len() - suffix].encode_utf16().count();
    Some(json!({ "offset": offset, "length": length, "text": &new[prefix..b.len() - suffix] }))
}

/// Host legend mapped onto the GPUI Kit highlight-theme vocabulary (index-aligned with
/// `Daedalus.LanguageServices.Protocol.TokenTypes.All`).
const LEGEND: &[&str] = &[
    "variable",
    "type",
    "type",
    "enum",
    "type",
    "type",
    "type", // namespace..typeParameter
    "variable",
    "variable",
    "property",
    "constant",
    "property",
    "function",
    "function", // parameter..method
    "preproc",
    "keyword",
    "keyword",
    "comment",
    "string",
    "number",
    "string.regex",
    "operator", // macro..operator
    "type",
    "type",
    "type",
    "property",
    "constant",
    "variable",
    "label", // delegate..label
    "comment",
    "preproc",
    "punctuation",
    "string.escape",
    "comment.doc", // excludedCode..xmlDocComment
    "keyword",
    "function",
    "string", // controlKeyword, extensionMethod, stringVerbatim
];

/// Semantic tokens for a window of lines around the viewport, fetched from the host.
pub struct WindowedTokensProvider {
    pub sync: DocSync,
    /// Visible rows, written by the editor panel on every render.
    pub visible: Rc<Cell<(usize, usize)>>,
    /// Lines covered by the last request.
    pub covered: Rc<Cell<(usize, usize)>>,
    /// Set once any token response has been received.
    pub received: Rc<Cell<bool>>,
}

impl DocumentRangeSemanticTokensProvider for WindowedTokensProvider {
    fn legend(&self) -> SemanticTokensLegend {
        SemanticTokensLegend {
            token_types: LEGEND.iter().map(|name| SemanticTokenType::new(name)).collect(),
            token_modifiers: vec![],
        }
    }

    fn semantic_tokens(
        &self,
        text: &Rope,
        _range: Range<usize>,
        _window: &mut Window,
        cx: &mut App,
    ) -> Task<Result<SemanticTokens>> {
        // The editor asks for the whole document; Roslyn needs seconds for that on large files,
        // so only the viewport ± a margin is requested (see vendor/PATCHES.md).
        let (first, last) = self.visible.get();
        let line_count = text.lines_len().max(1);
        let lines =
            first.saturating_sub(TOKEN_WINDOW_MARGIN)..(last.max(first + 1) + TOKEN_WINDOW_MARGIN).min(line_count);
        self.covered.set((lines.start, lines.end));
        let request = self.sync.tokens(text.clone(), lines);
        let received = self.received.clone();
        cx.spawn(async move |cx| {
            let value = request.await?;
            let data = value.get("data").and_then(Value::as_array).cloned().unwrap_or_default();
            let ints: Vec<u32> = data.iter().filter_map(Value::as_u64).map(|v| v as u32).collect();
            let tokens = ints
                .as_chunks::<5>()
                .0
                .iter()
                .map(|t| SemanticToken {
                    delta_line: t[0],
                    delta_start: t[1],
                    length: t[2],
                    token_type: t[3],
                    token_modifiers_bitset: t[4],
                })
                .collect();
            cx.update(|_| received.set(true));
            Ok(SemanticTokens {
                result_id: None,
                data: tokens,
            })
        })
    }
}

#[cfg(test)]
mod tests {
    use super::diff;
    use serde_json::json;

    #[test]
    fn diff_insert_in_middle() {
        assert_eq!(
            diff("class A {}", "class AB {}"),
            Some(json!({"offset": 7, "length": 0, "text": "B"}))
        );
    }

    #[test]
    fn diff_delete_and_replace() {
        assert_eq!(
            diff("abcdef", "abXf"),
            Some(json!({"offset": 2, "length": 3, "text": "X"}))
        );
        assert_eq!(diff("aaa", "aa"), Some(json!({"offset": 2, "length": 1, "text": ""})));
    }

    #[test]
    fn diff_uses_utf16_offsets() {
        // '😀' is 4 UTF-8 bytes but 2 UTF-16 code units.
        assert_eq!(
            diff("😀x", "😀yx"),
            Some(json!({"offset": 2, "length": 0, "text": "y"}))
        );
        assert_eq!(diff("é", "è"), Some(json!({"offset": 0, "length": 1, "text": "è"})));
    }

    #[test]
    fn diff_identical_is_none() {
        assert_eq!(diff("same", "same"), None);
    }
}
