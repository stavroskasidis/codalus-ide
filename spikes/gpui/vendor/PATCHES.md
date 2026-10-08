# Vendored crates

## gpui-base 0.7.1 (Apache-2.0, © Longbridge)

Copied unmodified from crates.io, except for the change below. It is wired in through
`[patch.crates-io]` in `spikes/gpui/Cargo.toml`.

### Change: `EditorState::refresh_semantic_tokens`

File: `src/input/editor/mod.rs`. This adds a public method that re-runs the editor's
semantic-tokens provider without a text edit.

**Why:** in 0.7.1 the editor re-fetches semantic tokens only after a text change, and it
always asks the provider for the *whole document*. On the 100k-line fixture, Roslyn takes
1.6–5.6 s (warm) to classify the full document and returns 549k tokens. A 150-line range
takes 5–50 ms. The spike's provider therefore returns tokens for a window around the
visible rows, and it needs a way to re-fetch when the user scrolls out of that window.
Upstream has no public hook for that.

This is an integration cost of prototype B, and docs/adr/0001-ui-stack.md records it. If
GPUI is chosen, the change (or range-aware fetching) should be proposed upstream.
