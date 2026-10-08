//! JSON-RPC 2.0 client for `daedalus-lshost` over stdio with LSP `Content-Length` framing.
//!
//! One writer thread serializes every outgoing message in call order (so edits always precede
//! the requests that depend on them); one reader thread completes pending requests. When the
//! host dies, pending requests fail and a [`HostEvent::Crashed`] is published; the owner decides
//! whether to [`HostClient::restart`]. Each (re)start bumps [`HostClient::generation`] so document
//! owners can lazily replay their state.

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::PathBuf;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc;
use std::sync::{Arc, Mutex};

use anyhow::{Context as _, Result, anyhow};
use futures::channel::oneshot;
use serde_json::{Value, json};

#[derive(Debug, Clone)]
pub enum HostEvent {
    Crashed { generation: u64, exit_code: Option<i32> },
    Log(String),
}

#[derive(Debug, Clone)]
pub struct HostLaunch {
    pub program: PathBuf,
    pub args: Vec<String>,
}

impl HostLaunch {
    /// `DAEDALUS_LSHOST` (apphost or .dll), else `lshost/` next to this executable.
    pub fn resolve() -> Option<Self> {
        let mut candidates = Vec::new();
        if let Ok(path) = std::env::var("DAEDALUS_LSHOST") {
            candidates.push(PathBuf::from(path));
        }
        if let Ok(exe) = std::env::current_exe()
            && let Some(dir) = exe.parent()
        {
            let name = if cfg!(windows) {
                "daedalus-lshost.exe"
            } else {
                "daedalus-lshost"
            };
            candidates.push(dir.join("lshost").join(name));
            candidates.push(dir.join("lshost").join("daedalus-lshost.dll"));
        }
        candidates.into_iter().find(|p| p.exists()).map(|path| {
            if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("dll")) {
                HostLaunch {
                    program: "dotnet".into(),
                    args: vec![path.to_string_lossy().into_owned()],
                }
            } else {
                HostLaunch {
                    program: path,
                    args: vec![],
                }
            }
        })
    }
}

type Pending = HashMap<u64, oneshot::Sender<Result<Value>>>;

struct Connection {
    writer: mpsc::Sender<Vec<u8>>,
    child: Child,
}

pub struct HostClient {
    launch: HostLaunch,
    events: mpsc::Sender<HostEvent>,
    pending: Arc<Mutex<Pending>>,
    connection: Mutex<Option<Connection>>,
    next_id: AtomicU64,
    generation: AtomicU64,
}

impl HostClient {
    pub fn new(launch: HostLaunch, events: mpsc::Sender<HostEvent>) -> Arc<Self> {
        Arc::new(Self {
            launch,
            events,
            pending: Arc::new(Mutex::new(HashMap::new())),
            connection: Mutex::new(None),
            next_id: AtomicU64::new(1),
            generation: AtomicU64::new(0),
        })
    }

    pub fn generation(&self) -> u64 {
        self.generation.load(Ordering::SeqCst)
    }

    /// Starts (or restarts) the host process and returns the new generation.
    pub fn restart(&self) -> Result<u64> {
        let mut command = Command::new(&self.launch.program);
        command.args(&self.launch.args);
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt as _;
            const CREATE_NO_WINDOW: u32 = 0x0800_0000;
            command.creation_flags(CREATE_NO_WINDOW);
        }
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .with_context(|| format!("failed to start {}", self.launch.program.display()))?;
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;

        let stdin = child.stdin.take().ok_or_else(|| anyhow!("no stdin"))?;
        let stdout = child.stdout.take().ok_or_else(|| anyhow!("no stdout"))?;
        let stderr = child.stderr.take().ok_or_else(|| anyhow!("no stderr"))?;
        let (tx, rx) = mpsc::channel::<Vec<u8>>();
        std::thread::Builder::new()
            .name("lshost-writer".into())
            .spawn(move || writer_loop(stdin, rx))?;

        let pending = self.pending.clone();
        let events = self.events.clone();
        std::thread::Builder::new()
            .name("lshost-reader".into())
            .spawn(move || {
                reader_loop(stdout, &pending);
                // The stream ended: fail everything in flight and report the crash.
                for (_, sender) in pending.lock().unwrap().drain() {
                    let _ = sender.send(Err(anyhow!("language service host connection lost")));
                }
                let _ = events.send(HostEvent::Crashed {
                    generation,
                    exit_code: None,
                });
            })?;
        let events = self.events.clone();
        std::thread::Builder::new()
            .name("lshost-stderr".into())
            .spawn(move || {
                for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                    let _ = events.send(HostEvent::Log(line));
                }
            })?;

        let mut connection = self.connection.lock().unwrap();
        if let Some(mut old) = connection.take() {
            let _ = old.child.kill();
        }
        *connection = Some(Connection { writer: tx, child });
        Ok(generation)
    }

    /// Sends a request; the returned future resolves with the `result` value.
    pub fn request(&self, method: &str, params: Value) -> impl Future<Output = Result<Value>> + Send + 'static {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let (tx, rx) = oneshot::channel();
        self.pending.lock().unwrap().insert(id, tx);
        let sent = self.send(json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params }));
        if sent.is_err() {
            self.pending.lock().unwrap().remove(&id);
        }
        async move {
            sent?;
            rx.await.map_err(|_| anyhow!("request cancelled"))?
        }
    }

    pub fn notify(&self, method: &str, params: Value) -> Result<()> {
        self.send(json!({ "jsonrpc": "2.0", "method": method, "params": params }))
    }

    fn send(&self, message: Value) -> Result<()> {
        let body = serde_json::to_vec(&message)?;
        let connection = self.connection.lock().unwrap();
        let connection = connection
            .as_ref()
            .ok_or_else(|| anyhow!("language service host is not running"))?;
        connection
            .writer
            .send(body)
            .map_err(|_| anyhow!("language service host connection lost"))
    }

    pub fn shutdown(&self) {
        let _ = self.notify("exit", Value::Null);
        if let Some(mut connection) = self.connection.lock().unwrap().take() {
            std::thread::sleep(std::time::Duration::from_millis(100));
            let _ = connection.child.kill();
        }
    }
}

fn writer_loop(mut stdin: ChildStdin, rx: mpsc::Receiver<Vec<u8>>) {
    for body in rx {
        let header = format!("Content-Length: {}\r\n\r\n", body.len());
        if stdin
            .write_all(header.as_bytes())
            .and_then(|_| stdin.write_all(&body))
            .and_then(|_| stdin.flush())
            .is_err()
        {
            break;
        }
    }
}

fn reader_loop(stdout: impl Read, pending: &Mutex<Pending>) {
    let mut reader = BufReader::new(stdout);
    loop {
        let mut length = None;
        loop {
            let mut line = String::new();
            match reader.read_line(&mut line) {
                Ok(0) | Err(_) => return,
                Ok(_) => {}
            }
            let line = line.trim_end();
            if line.is_empty() {
                break;
            }
            if let Some(value) = line.strip_prefix("Content-Length:") {
                length = value.trim().parse::<usize>().ok();
            }
        }
        let Some(length) = length else { return };
        let mut body = vec![0u8; length];
        if reader.read_exact(&mut body).is_err() {
            return;
        }
        let Ok(message) = serde_json::from_slice::<Value>(&body) else {
            continue;
        };
        if message.get("method").is_some() {
            continue; // Server-to-client requests are not used by the M0 protocol.
        }
        let Some(id) = message.get("id").and_then(Value::as_u64) else {
            continue;
        };
        let Some(sender) = pending.lock().unwrap().remove(&id) else {
            continue;
        };
        let result = match message.get("error") {
            Some(error) => Err(anyhow!(
                "host error {}: {}",
                error.get("code").and_then(Value::as_i64).unwrap_or(0),
                error.get("message").and_then(Value::as_str).unwrap_or("unknown")
            )),
            None => Ok(message.get("result").cloned().unwrap_or(Value::Null)),
        };
        let _ = sender.send(result);
    }
}
