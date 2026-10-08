//! Benchmark events as JSON lines, in the same format as `Daedalus.Diagnostics.BenchEvents` (C#):
//! every line has `ev`, `wall_us` (Unix epoch µs, comparable across processes) and `mono_us`
//! (process-local monotonic µs). The file comes from `DAEDALUS_BENCH_EVENTS`.

use std::fs::{File, OpenOptions};
use std::io::Write;
use std::sync::{Mutex, OnceLock};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

pub const MAIN_ENTERED: &str = "main_entered";
pub const FIRST_FRAME: &str = "first_frame";
pub const INTERACTIVE: &str = "interactive";
pub const SOLUTION_LOADED: &str = "solution_loaded";
pub const HIGHLIGHT_READY: &str = "highlight_ready";
pub const KEY_LATENCY: &str = "key_latency";
pub const SCROLL_FRAME: &str = "scroll_frame";
pub const SCENARIO_DONE: &str = "scenario_done";

struct Sink {
    file: Option<Mutex<File>>,
    origin: Instant,
}

fn sink() -> &'static Sink {
    static SINK: OnceLock<Sink> = OnceLock::new();
    SINK.get_or_init(|| {
        let file = std::env::var("DAEDALUS_BENCH_EVENTS").ok().and_then(|path| {
            if let Some(parent) = std::path::Path::new(&path).parent() {
                let _ = std::fs::create_dir_all(parent);
            }
            OpenOptions::new().create(true).append(true).open(path).ok().map(Mutex::new)
        });
        Sink { file, origin: Instant::now() }
    })
}

/// Initializes the monotonic origin; call first thing in `main`.
pub fn init() {
    let _ = sink();
}

pub fn mono_us() -> i64 {
    sink().origin.elapsed().as_micros() as i64
}

pub fn emit(name: &str, fields: &[(&str, i64)]) {
    let sink = sink();
    let Some(file) = &sink.file else { return };
    let wall = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_micros() as i64).unwrap_or(0);
    let mut line = format!(r#"{{"ev":"{name}","wall_us":{wall},"mono_us":{}"#, mono_us());
    for (key, value) in fields {
        line.push_str(&format!(r#","{key}":{value}"#));
    }
    line.push_str("}\n");
    if let Ok(mut file) = file.lock() {
        let _ = file.write_all(line.as_bytes());
        let _ = file.flush();
    }
}
