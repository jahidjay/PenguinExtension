//! Real subprocess tests: official MCP newline framing, never Content-Length.
use serde_json::{json, Value};
use std::{path::Path, process::Stdio, time::Duration};
use tokio::{
    io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader},
    process::{Child, ChildStdin, ChildStdout, Command},
};

const WAIT: Duration = Duration::from_secs(15);
struct Client {
    child: Child,
    input: Option<ChildStdin>,
    output: BufReader<ChildStdout>,
    id: u64,
}
impl Client {
    async fn spawn(roots: &[&Path], extra: &[&str]) -> Self {
        let mut cmd = Command::new(env!("CARGO_BIN_EXE_penguin-mcp"));
        for root in roots {
            cmd.arg("--root").arg(root);
        }
        let mut child = cmd
            .args(extra)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true)
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let output = BufReader::new(child.stdout.take().unwrap());
        Self {
            child,
            input,
            output,
            id: 0,
        }
    }
    async fn send(&mut self, value: Value) {
        let mut text = serde_json::to_vec(&value).unwrap();
        text.push(b'\n');
        self.input.as_mut().unwrap().write_all(&text).await.unwrap();
        self.input.as_mut().unwrap().flush().await.unwrap();
    }
    async fn receive(&mut self) -> Value {
        let mut line = String::new();
        let count = tokio::time::timeout(WAIT, self.output.read_line(&mut line))
            .await
            .expect("stdout timeout")
            .unwrap();
        assert!(count > 0, "unexpected stdout EOF");
        assert!(
            !line.starts_with("Content-Length"),
            "LSP framing is not MCP"
        );
        serde_json::from_str(&line).expect("stdout must contain JSON-RPC lines only")
    }
    async fn request(&mut self, method: &str, params: Value) -> Value {
        self.id += 1;
        let id = self.id;
        self.send(json!({"jsonrpc":"2.0","id":id,"method":method,"params":params}))
            .await;
        loop {
            let value = self.receive().await;
            if value["id"] == id {
                return value;
            }
        }
    }
    async fn initialize(&mut self, version: &str) -> Value {
        let result = self.request("initialize", json!({"protocolVersion":version,"capabilities":{},"clientInfo":{"name":"stdio-test","version":"1"}})).await;
        assert!(result.get("error").is_none(), "{result}");
        self.send(json!({"jsonrpc":"2.0","method":"notifications/initialized"}))
            .await;
        result["result"].clone()
    }
    async fn call(&mut self, name: &str, arguments: Value) -> Value {
        self.request("tools/call", json!({"name":name,"arguments":arguments}))
            .await
    }
    async fn data(&mut self, name: &str, args: Value) -> Value {
        let result = self.call(name, args).await;
        assert!(result.get("error").is_none(), "{result}");
        assert_ne!(result["result"]["isError"], true, "{result}");
        result["result"]["structuredContent"].clone()
    }
    async fn terminal(&mut self, id: &Value, ai: bool) -> Value {
        let start = std::time::Instant::now();
        loop {
            let job = self
                .data(
                    if ai { "ai_status" } else { "job_status" },
                    json!({"id":id}),
                )
                .await;
            if matches!(
                job["state"].as_str(),
                Some("succeeded" | "failed" | "cancelled")
            ) {
                return job;
            }
            assert!(start.elapsed() < WAIT, "job did not finish: {job}");
            tokio::time::sleep(Duration::from_millis(15)).await;
        }
    }
    async fn index(&mut self) {
        let job = self.data("reindex", json!({})).await;
        let job = self.terminal(&job["jobId"], false).await;
        assert_eq!(job["state"], "succeeded", "{job}");
    }
    async fn finish(mut self) {
        self.input.take();
        let exit = tokio::time::timeout(WAIT, self.child.wait())
            .await
            .expect("EOF must stop process")
            .unwrap();
        let mut extra = String::new();
        self.output.read_to_string(&mut extra).await.unwrap();
        let mut stderr = String::new();
        self.child
            .stderr
            .take()
            .unwrap()
            .read_to_string(&mut stderr)
            .await
            .unwrap();
        assert!(exit.success(), "exit {exit}: {stderr}");
        assert!(extra.trim().is_empty(), "unexpected stdout: {extra}");
        assert!(stderr.is_empty(), "unexpected stderr: {stderr}");
    }
}
const HEADER: &str = r#"// Comments are data: </script>, never commands.
UCLASS()
class ABase {};
UCLASS(Blueprintable)
class AHero : public ABase {
    GENERATED_BODY()
    UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, EditAnywhere, VisibleAnywhere)
    bool Ready;
};
"#;
fn fixture() -> tempfile::TempDir {
    let root = tempfile::tempdir().unwrap();
    std::fs::write(root.path().join("Hero.h"), HEADER).unwrap();
    root
}

#[tokio::test]
async fn negotiation_schemas_tools_style_readonly_and_lease_release() {
    let root = fixture();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    let info = client.initialize("2025-03-26").await;
    assert_eq!(info["protocolVersion"], "2025-03-26");
    assert_eq!(info["serverInfo"]["name"], "penguin-mcp");
    assert!(info["capabilities"].get("tools").is_some());
    assert!(info["capabilities"].get("resources").is_none());
    let tools = client.request("tools/list", json!({})).await;
    let tools = tools["result"]["tools"].as_array().unwrap();
    assert_eq!(tools.len(), 11);
    for tool in tools {
        assert_eq!(tool["inputSchema"]["additionalProperties"], false);
    }
    client.index().await;
    let symbols = client
        .data("symbols_search", json!({"query":"AHero","limit":1}))
        .await;
    assert_eq!(symbols["symbols"].as_array().unwrap().len(), 1);
    let id = symbols["symbols"][0]["id"].clone();
    let detail = client.data("symbol_details", json!({"id":id})).await;
    assert_eq!(detail["symbol"]["name"], "AHero");
    let bases = client.data("inheritance", json!({"id":id})).await;
    assert_eq!(bases["bases"][0], "ABase");
    let job = client
        .data("style_check", json!({"id":id,"naming":true}))
        .await;
    let job = client.terminal(&job["jobId"], false).await;
    assert_eq!(job["state"], "succeeded", "{job}");
    let diags = job["result"]["diagnostics"].as_array().unwrap();
    assert!(diags.iter().any(|d| d["code"] == "UE_STYLE_001"));
    assert!(diags.iter().any(|d| d["code"] == "UE_STYLE_002"));
    let ai = client
        .call(
            "ai_start",
            json!({"task":"explain","source":"UPROPERTY() int Health;"}),
        )
        .await;
    assert_eq!(ai["result"]["isError"], true);
    assert_eq!(
        ai["result"]["structuredContent"]["error"]["code"],
        "aiDisabled"
    );
    client.index().await;
    let stale = client.call("symbol_details", json!({"id":id})).await;
    assert_eq!(stale["result"]["isError"], true);
    assert_eq!(
        stale["result"]["structuredContent"]["error"]["code"],
        "staleReference"
    );
    client.finish().await;
    assert_eq!(
        std::fs::read_to_string(root.path().join("Hero.h")).unwrap(),
        HEADER
    );
    // Same namespace must be available after graceful EOF.
    let mut next = Client::spawn(&[root.path()], &[]).await;
    next.initialize("2025-03-26").await;
    next.finish().await;
}

#[tokio::test]
async fn invalid_inputs_path_escape_multiroot_and_version_fallback() {
    let root = fixture();
    let second = tempfile::tempdir().unwrap();
    let outside = tempfile::tempdir().unwrap();
    std::fs::write(second.path().join("Second.h"), "UCLASS() class USecond {};").unwrap();
    std::fs::write(
        outside.path().join("Secret.h"),
        "UCLASS() class USecret {};",
    )
    .unwrap();
    let mut client = Client::spawn(&[root.path(), second.path()], &[]).await;
    let info = client.initialize("1900-01-01").await;
    assert_ne!(info["protocolVersion"], "1900-01-01");
    for (name, args) in [
        ("symbols_search", json!({"query":"x","limit":0})),
        ("symbols_search", json!({"query":"x","limit":201})),
        ("symbols_search", json!({"query":"x","limit":1.5})),
        ("symbols_search", json!({"query":"x","path":outside.path()})),
        ("index_status", json!({"root":outside.path()})),
        ("reindex", json!({"command":"echo unsafe"})),
        ("ai_start", json!({"task":"execute","source":"x"})),
        ("missing_tool", json!({})),
    ] {
        let response = client.call(name, args).await;
        assert_eq!(response["error"]["code"], -32602, "{response}");
    }
    let escaped = client
        .call(
            "symbol_details",
            json!({"id":outside.path().join("Secret.h")}),
        )
        .await;
    assert_eq!(escaped["result"]["isError"], true);
    client.index().await;
    let found = client
        .data("symbols_search", json!({"query":"USecond"}))
        .await;
    assert_eq!(found["symbols"].as_array().unwrap().len(), 1);
    let found = client
        .data("symbols_search", json!({"query":"USecret"}))
        .await;
    assert!(found["symbols"].as_array().unwrap().is_empty());
    // Quotes, slash, newlines and Unicode are a single JSON parameter, not protocol.
    let found = client
        .data(
            "symbols_search",
            json!({"query":"\"\n</script>\\../../\u{96ea}"}),
        )
        .await;
    assert!(found["symbols"].as_array().unwrap().is_empty());
    let response = client.request("ping", json!({})).await;
    assert!(response.get("error").is_none());
    client.finish().await;
}

#[tokio::test]
async fn disabled_or_invalid_launch_has_no_stdout_or_network() {
    let root = fixture();
    for extra in [
        vec!["--model", "qwen2.5-coder:3b"],
        vec!["--ai", "--endpoint", "https://example.com"],
        vec!["--unknown"],
    ] {
        let output = Command::new(env!("CARGO_BIN_EXE_penguin-mcp"))
            .arg("--root")
            .arg(root.path())
            .args(extra)
            .output()
            .await
            .unwrap();
        assert!(!output.status.success());
        assert!(output.stdout.is_empty());
        assert!(!output.stderr.is_empty());
    }
    let output = Command::new(env!("CARGO_BIN_EXE_penguin-mcp"))
        .arg("--help")
        .output()
        .await
        .unwrap();
    assert!(output.status.success());
    assert!(output.stdout.is_empty());
}

#[tokio::test]
async fn ai_queue_cancel_sdk_notification_and_eof_drop_http() {
    let root = fixture();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let endpoint = format!("http://{}", listener.local_addr().unwrap());
    let (accepted_tx, mut accepted_rx) = tokio::sync::mpsc::channel(8);
    let (closed_tx, mut closed_rx) = tokio::sync::mpsc::channel(8);
    let mock = tokio::spawn(async move {
        loop {
            let (mut stream, _) = listener.accept().await.unwrap();
            let accepted = accepted_tx.clone();
            let closed = closed_tx.clone();
            tokio::spawn(async move {
                let mut buf = [0; 4096];
                let mut first = true;
                loop {
                    match stream.read(&mut buf).await {
                        Ok(0) | Err(_) => {
                            let _ = closed.send(()).await;
                            break;
                        }
                        Ok(_) => {
                            if first {
                                first = false;
                                let _ = accepted.send(()).await;
                            }
                        }
                    }
                }
            });
        }
    });
    let mut client = Client::spawn(&[root.path()], &["--ai", "--endpoint", &endpoint]).await;
    client.initialize("2025-03-26").await;
    let mut jobs = Vec::new();
    for _ in 0..4 {
        jobs.push(
            client
                .data(
                    "ai_start",
                    json!({"task":"explain","source":"UCLASS() class UTest {};"}),
                )
                .await,
        );
    }
    tokio::time::timeout(WAIT, accepted_rx.recv())
        .await
        .unwrap()
        .unwrap();
    let full = client
        .call("ai_start", json!({"task":"explain","source":"x"}))
        .await;
    assert_eq!(
        full["result"]["structuredContent"]["error"]["code"],
        "queueFull"
    );
    client
        .data("ai_cancel", json!({"id":jobs[0]["jobId"]}))
        .await;
    assert_eq!(
        client.terminal(&jobs[0]["jobId"], true).await["state"],
        "cancelled"
    );
    tokio::time::timeout(WAIT, closed_rx.recv())
        .await
        .unwrap()
        .unwrap();
    // Native MCP cancellation notification is handled by the SDK, without stdout logs.
    client.send(json!({"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":9999,"reason":"cancel test"}})).await;
    assert!(client
        .request("ping", json!({}))
        .await
        .get("error")
        .is_none());
    // EOF cancels remaining queued/running inference, releases writer, and exits.
    client.finish().await;
    tokio::time::timeout(WAIT, closed_rx.recv())
        .await
        .unwrap()
        .unwrap();
    mock.abort();
    let mut next = Client::spawn(&[root.path()], &[]).await;
    next.initialize("2025-03-26").await;
    next.finish().await;
}

#[tokio::test]
async fn cancellation_during_job_submission_leaves_no_active_orphan() {
    let root = fixture();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    client.initialize("2025-03-26").await;
    for _ in 0..64 {
        client.id += 1;
        let id = client.id;
        client
            .send(json!({"jsonrpc":"2.0","id":id,"method":"tools/call","params":{"name":"reindex","arguments":{}}}))
            .await;
        client
            .send(json!({"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":id,"reason":"race test"}}))
            .await;
        // The SDK may suppress a cancelled response. A subsequent ping is the
        // protocol barrier; do not require a reply to the cancelled request.
        assert!(client
            .request("ping", json!({}))
            .await
            .get("error")
            .is_none());
    }
    tokio::time::timeout(WAIT, async {
        loop {
            let status = client.data("index_status", json!({})).await;
            if status["queuedJobs"] == 0 && status["runningJobs"] == 0 {
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("cancelled submissions must not leave active orphan jobs");
    client.finish().await;
}

#[tokio::test]
async fn local_mock_ai_preview_and_unavailable_service() {
    let root = fixture();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let endpoint = format!("http://{}", listener.local_addr().unwrap());
    let preview = "// Preview only </script>\nint Answer() { return 42; }\n";
    let mock = tokio::spawn(async move {
        let (stream, _) = listener.accept().await.unwrap();
        let mut stream = BufReader::new(stream);
        let mut length = 0;
        loop {
            let mut line = String::new();
            stream.read_line(&mut line).await.unwrap();
            if line.to_ascii_lowercase().starts_with("content-length:") {
                length = line.split_once(':').unwrap().1.trim().parse().unwrap();
            }
            if line == "\r\n" {
                break;
            }
        }
        let mut body = vec![0; length];
        stream.read_exact(&mut body).await.unwrap();
        let request: Value = serde_json::from_slice(&body).unwrap();
        assert_eq!(request["model"], "qwen2.5-coder:3b");
        assert_eq!(request["stream"], false);
        let content: Value = serde_json::from_str(request["prompt"].as_str().unwrap()).unwrap();
        assert_eq!(
            content["source"],
            "// untrusted </script>\nUCLASS() class UTest {};"
        );
        let body =
            json!({"response":preview,"done":true,"eval_count":20,"model":"qwen2.5-coder:3b"})
                .to_string();
        let response=format!("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",body.len(),body);
        stream
            .get_mut()
            .write_all(response.as_bytes())
            .await
            .unwrap();
    });
    let mut client = Client::spawn(&[root.path()], &["--ai", "--endpoint", &endpoint]).await;
    client.initialize("2025-03-26").await;
    let job=client.data("ai_start",json!({"task":"generate","source":"// untrusted </script>\nUCLASS() class UTest {};","instruction":"propose a method"})).await;
    let result = client.terminal(&job["jobId"], true).await;
    assert_eq!(result["state"], "succeeded", "{result}");
    assert_eq!(result["result"]["text"], preview);
    assert_eq!(result["result"]["previewOnly"], true);
    mock.await.unwrap();
    // The mock listener has closed; no installed/running model is required.
    let job = client
        .data("ai_start", json!({"task":"explain","source":"x"}))
        .await;
    let result = client.terminal(&job["jobId"], true).await;
    assert_eq!(result["state"], "failed");
    assert!(result["error"]["message"]
        .as_str()
        .unwrap()
        .contains("unavailable"));
    client.finish().await;
}

#[tokio::test]
async fn oversized_stdio_line_closes_and_releases_namespace() {
    let root = fixture();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    client.initialize("2025-03-26").await;
    let bytes = vec![b'x'; ue_mcp::transport::MAX_MESSAGE_BYTES + 1];
    let _ = client.input.as_mut().unwrap().write_all(&bytes).await;
    client.finish().await;
    let mut next = Client::spawn(&[root.path()], &[]).await;
    next.initialize("2025-03-26").await;
    next.finish().await;
}

#[tokio::test]
async fn eof_during_initialization_exits_and_releases_namespace() {
    let root = fixture();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    client.input.take();
    let _ = tokio::time::timeout(WAIT, client.child.wait())
        .await
        .expect("initial EOF exit")
        .unwrap();
    let mut stdout = String::new();
    client.output.read_to_string(&mut stdout).await.unwrap();
    assert!(stdout.is_empty());
    let mut next = Client::spawn(&[root.path()], &[]).await;
    next.initialize("2025-03-26").await;
    next.finish().await;
}

#[tokio::test]
async fn cancelled_style_job_is_polled_and_eof_cancels_queued_index_work() {
    let root = fixture();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    client.initialize("2025-03-26").await;
    client.index().await;
    let search = client
        .data("symbols_search", json!({"query":"AHero"}))
        .await;
    let id = search["symbols"][0]["id"].clone();
    let job = client.data("style_check", json!({"id":id})).await;
    let cancelled = client.data("job_cancel", json!({"id":job["jobId"]})).await;
    assert_eq!(cancelled["jobId"], job["jobId"]);
    let terminal = client.terminal(&job["jobId"], false).await;
    assert!(matches!(
        terminal["state"].as_str(),
        Some("cancelled" | "succeeded")
    ));
    // Enqueue another disk scan then close the pipe without polling it.
    client.data("reindex", json!({})).await;
    client.finish().await;
    let mut next = Client::spawn(&[root.path()], &[]).await;
    next.initialize("2025-03-26").await;
    next.finish().await;
}

#[tokio::test]
async fn style_rejects_indexed_file_replaced_by_directory_junction() {
    let root = fixture();
    let outside = tempfile::tempdir().unwrap();
    let nested = root.path().join("Nested");
    std::fs::create_dir(&nested).unwrap();
    std::fs::write(nested.join("Escape.h"), "UCLASS() class UEscape {};").unwrap();
    std::fs::write(
        outside.path().join("Escape.h"),
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Secret;",
    )
    .unwrap();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    client.initialize("2025-03-26").await;
    client.index().await;
    let search = client
        .data("symbols_search", json!({"query":"UEscape"}))
        .await;
    let id = search["symbols"][0]["id"].clone();
    std::fs::remove_dir_all(&nested).unwrap();
    #[cfg(windows)]
    {
        let status = Command::new("cmd.exe")
            .args(["/d", "/c", "mklink", "/J"])
            .arg(&nested)
            .arg(outside.path())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
            .await
            .unwrap();
        assert!(status.success(), "fixture junction creation failed");
    }
    #[cfg(unix)]
    std::os::unix::fs::symlink(outside.path(), &nested).unwrap();
    let result = client.call("style_check", json!({"id":id})).await;
    assert_eq!(result["result"]["isError"], true, "{result}");
    assert_eq!(
        result["result"]["structuredContent"]["error"]["code"],
        "excludedPath"
    );
    client.finish().await;
    #[cfg(windows)]
    std::fs::remove_dir(&nested).unwrap();
    #[cfg(unix)]
    std::fs::remove_file(&nested).unwrap();
    assert!(outside.path().join("Escape.h").exists());
}

#[tokio::test]
async fn mcp_disk_symbols_match_shared_core_queries() {
    let root = fixture();
    let core = ue_core::WorkspaceSession::open(ue_core::SessionConfig::new(
        vec![root.path().to_string_lossy().into()],
        "mcp-test-comparison",
    ))
    .await
    .unwrap();
    let job = core.reindex().unwrap();
    tokio::time::timeout(WAIT, async {
        loop {
            let status = core.job_status(&job.job_id).unwrap();
            if status.state.is_terminal() {
                assert_eq!(status.state, ue_core::JobState::Succeeded);
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    let direct = core
        .search(ue_core::SearchRequest {
            api_version: 1,
            query: String::new(),
            limit: 200,
        })
        .await
        .unwrap();
    let mut client = Client::spawn(&[root.path()], &[]).await;
    client.initialize("2025-03-26").await;
    client.index().await;
    let actual = client
        .data("symbols_search", json!({"query":"","limit":200}))
        .await;
    let actual = actual["symbols"].as_array().unwrap();
    assert_eq!(actual.len(), direct.symbols.len());
    for expected in direct.symbols {
        let actual = actual.iter().find(|s| s["name"] == expected.name).unwrap();
        assert_eq!(actual["kind"], expected.kind);
        assert_eq!(actual["line"], expected.line);
        assert_eq!(
            actual["byteRange"],
            serde_json::to_value(expected.byte_range).unwrap()
        );
        assert_eq!(
            actual["specifiers"],
            serde_json::to_value(expected.specifiers).unwrap()
        );
        assert_eq!(actual["bases"], json!(expected.bases));
    }
    client.finish().await;
    core.close(true).await.unwrap();
}
