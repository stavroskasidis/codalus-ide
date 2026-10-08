// GUI subsystem on Windows: no console window (prototype A behaves the same).
#![cfg_attr(windows, windows_subsystem = "windows")]

//! M0 UI stack spike, prototype B: a Rust shell on GPUI (via GPUI Kit) that talks to the same
//! out-of-process .NET language service host (`daedalus-lshost`) as prototype A.
//!
//! Command line: `[--solution path] [--open file] [--bench typing|scroll]
//! [--exit-on interactive|highlight|done]`.

mod bench;
mod doc_sync;
mod rpc;
mod scenarios;
mod workspace;

use gpui_kit::component::{Theme, ThemeMode};
use gpui_kit::*;

#[derive(Clone, Debug, Default)]
pub struct Options {
    pub solution: Option<String>,
    pub open: Option<String>,
    pub bench: Option<String>,
    pub exit_on: Option<String>,
}

impl Options {
    fn parse() -> Self {
        let args: Vec<String> = std::env::args().collect();
        let value = |name: &str| {
            args.iter()
                .position(|a| a == name)
                .and_then(|i| args.get(i + 1))
                .cloned()
        };
        let absolute = |p: Option<String>| {
            p.map(|p| {
                std::path::absolute(&p)
                    .map(|a| a.to_string_lossy().into_owned())
                    .unwrap_or(p)
            })
        };
        Self {
            solution: absolute(value("--solution")),
            open: absolute(value("--open")),
            bench: value("--bench"),
            exit_on: value("--exit-on"),
        }
    }
}

fn main() {
    bench::init();
    bench::emit(bench::MAIN_ENTERED, &[]);
    let options = Options::parse();

    gpui_kit::application()
        .with_assets(gpui_kit::assets::Assets)
        .run(move |cx| {
            gpui_kit::init(cx);
            Theme::change(ThemeMode::Dark, None, cx);
            cx.activate(true);

            let bounds = Bounds::centered(None, size(px(1280.), px(800.)), cx);
            let window_options = WindowOptions {
                window_bounds: Some(WindowBounds::Windowed(bounds)),
                titlebar: Some(TitlebarOptions {
                    title: Some("Daedalus Studio — spike B (GPUI)".into()),
                    ..Default::default()
                }),
                ..Default::default()
            };
            gpui_kit::open_window(window_options, cx, |window, cx| {
                cx.new(|cx| workspace::Workspace::new(options.clone(), window, cx))
            })
            .expect("failed to open window");
        });
}
