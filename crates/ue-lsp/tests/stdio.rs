//! End-to-end framing and lifecycle checks against the shipped binary.

use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::time::{Duration, Instant};

use serde_json::{json, Value};

struct Session {
    child: Child,
    input: ChildStdin,
    output: Receiver<Value>,
    next_id: u64,
    notifications: Vec<Value>,
}

impl Session {
    fn start() -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_penguin-lsp"))
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()
            .expect("launch penguin-lsp");
        let input = child.stdin.take().unwrap();
        let stdout = child.stdout.take().unwrap();
        let (sender, output) = mpsc::channel();
        std::thread::spawn(move || {
            let mut reader = BufReader::new(stdout);
            loop {
                let mut length = None;
                loop {
                    let mut line = String::new();
                    if reader.read_line(&mut line).unwrap() == 0 {
                        return;
                    }
                    if line == "\r\n" {
                        break;
                    }
                    if let Some(value) = line.strip_prefix("Content-Length: ") {
                        length = Some(value.trim().parse::<usize>().unwrap());
                    }
                }
                let mut body = vec![0; length.expect("stdout must contain only LSP frames")];
                reader.read_exact(&mut body).unwrap();
                if sender.send(serde_json::from_slice(&body).unwrap()).is_err() {
                    return;
                }
            }
        });
        Self {
            child,
            input,
            output,
            next_id: 0,
            notifications: Vec::new(),
        }
    }

    fn send(&mut self, mut message: Value) {
        // Parameterless methods omit params; tower-lsp rejects an explicit null.
        if message.get("params") == Some(&Value::Null) {
            message.as_object_mut().unwrap().remove("params");
        }
        let body = serde_json::to_vec(&message).unwrap();
        write!(self.input, "Content-Length: {}\r\n\r\n", body.len()).unwrap();
        self.input.write_all(&body).unwrap();
        self.input.flush().unwrap();
    }

    fn notify(&mut self, method: &str, params: Value) {
        self.send(json!({"jsonrpc":"2.0", "method":method, "params":params}));
    }

    fn request(&mut self, method: &str, params: Value) -> Value {
        let response = self.response(method, params);
        assert!(response.get("error").is_none(), "{response}");
        response["result"].clone()
    }

    fn response(&mut self, method: &str, params: Value) -> Value {
        self.next_id += 1;
        let id = self.next_id;
        self.send(json!({"jsonrpc":"2.0", "id":id, "method":method, "params":params}));
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            let response = self
                .output
                .recv_timeout(remaining)
                .expect("server response timeout");
            if response.get("id") == Some(&json!(id)) {
                return response;
            }
            self.notifications.push(response);
        }
    }

    fn wait_for_exit(&mut self, expected_code: i32) {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                assert_eq!(status.code(), Some(expected_code), "{status}");
                return;
            }
            assert!(Instant::now() < deadline, "server did not exit");
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    fn outline_named(&mut self, uri: &str, expected: &str) -> Value {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            let result = self.request(
                "textDocument/documentSymbol",
                json!({"textDocument":{"uri":uri}}),
            );
            if result
                .as_array()
                .is_some_and(|items| items.iter().any(|s| s["name"] == expected))
            {
                return result;
            }
            assert!(
                Instant::now() < deadline,
                "outline never contained {expected}: {result}"
            );
            std::thread::sleep(Duration::from_millis(10));
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
fn stdio_serves_editor_features_and_clean_shutdown_without_a_workspace() {
    let mut session = Session::start();
    let initialized = session.request("initialize", json!({"capabilities":{}}));
    assert_eq!(initialized["capabilities"]["hoverProvider"], true);
    assert_eq!(initialized["capabilities"]["definitionProvider"], true);
    session.notify("initialized", json!({}));
    let uri = "untitled:Actor.h";
    session.notify(
        "textDocument/didOpen",
        json!({"textDocument":{
            "uri":uri, "languageId":"cpp", "version":1,
            "text":"UCLASS() class AHero {};\nAHero* Hero;\nUPROPERTY(Edit"
        }}),
    );
    let outline = session.outline_named(uri, "AHero");
    assert_eq!(outline[0]["name"], "AHero");
    let at = |line, character| json!({"textDocument":{"uri":uri}, "position":{"line":line,"character":character}});
    let hover = session.request("textDocument/hover", at(1, 2));
    assert!(hover.to_string().contains("AHero"), "{hover}");
    let definition = session.request("textDocument/definition", at(1, 2));
    assert!(definition.to_string().contains(uri), "{definition}");
    let completion = session.request("textDocument/completion", at(2, 14));
    assert!(
        completion.to_string().contains("EditAnywhere"),
        "{completion}"
    );

    session.notify(
        "textDocument/didChange",
        json!({"textDocument":{"uri":uri,"version":2},
        "contentChanges":[{"text":"UCLASS() class AReplacement {};"}]}),
    );
    let outline = session.outline_named(uri, "AReplacement");
    assert!(!outline.to_string().contains("AHero"));
    session.notify("textDocument/didClose", json!({"textDocument":{"uri":uri}}));
    assert!(session.request("shutdown", Value::Null).is_null());
    session.notify("exit", Value::Null);
    session.wait_for_exit(0);
}

#[test]
fn exit_without_shutdown_returns_failure_even_before_initialize() {
    for initialize in [false, true] {
        let mut session = Session::start();
        if initialize {
            session.request("initialize", json!({"capabilities":{}}));
        }
        session.notify("exit", Value::Null);
        session.wait_for_exit(1);
    }
}

#[test]
fn malformed_exit_does_not_terminate_the_server() {
    let mut session = Session::start();
    session.request("initialize", json!({"capabilities":{}}));
    session.notify("exit", json!({}));
    assert!(session.request("shutdown", Value::Null).is_null());
    session.notify("exit", Value::Null);
    session.wait_for_exit(0);
}

#[test]
fn workspace_disk_symbols_are_shadowed_by_unsaved_buffers() {
    use tower_lsp::lsp_types::Url;
    let temp = tempfile::tempdir().unwrap();
    let header = temp.path().join("Actor.h");
    std::fs::write(&header, "UCLASS() class AOnDisk {};").unwrap();
    let root = Url::from_directory_path(temp.path()).unwrap().to_string();
    let uri = Url::from_file_path(&header).unwrap().to_string();
    let mut session = Session::start();
    session.request("initialize", json!({"capabilities":{}, "rootUri":root}));
    session.notify("initialized", json!({}));
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        let found = session.request("workspace/symbol", json!({"query":"AOnDisk"}));
        if found.as_array().is_some_and(|rows| !rows.is_empty()) {
            // Keep the client's root spelling, including Windows 8.3 aliases.
            assert_eq!(found[0]["location"]["uri"], uri);
            break;
        }
        assert!(
            Instant::now() < deadline,
            "workspace scan never found header"
        );
        std::thread::sleep(Duration::from_millis(10));
    }
    session.notify(
        "textDocument/didOpen",
        json!({"textDocument":{
            "uri":uri,"languageId":"cpp","version":1,"text":"UCLASS() class AUnsaved {};"
        }}),
    );
    session.outline_named(&uri, "AUnsaved");
    let stale = session.request("workspace/symbol", json!({"query":"AOnDisk"}));
    assert_eq!(stale, json!([]));
    let live = session.request("workspace/symbol", json!({"query":"AUnsaved"}));
    assert_eq!(live[0]["name"], "AUnsaved");

    // Cross-file requests see the live declaration, not the disk version.
    let usage = "untitled:Usage.cpp";
    session.notify(
        "textDocument/didOpen",
        json!({"textDocument":{
            "uri":usage,"languageId":"cpp","version":1,"text":"AUnsaved* Actor;"
        }}),
    );
    let request = json!({"textDocument":{"uri":usage},"position":{"line":0,"character":2}});
    let hover = session.request("textDocument/hover", request.clone());
    assert!(hover.to_string().contains("AUnsaved"));
    let definition = session.request("textDocument/definition", request);
    assert_eq!(definition[0]["uri"], uri);

    session.notify("textDocument/didClose", json!({"textDocument":{"uri":uri}}));
    let disk = session.request("workspace/symbol", json!({"query":"AOnDisk"}));
    assert_eq!(disk[0]["name"], "AOnDisk");
    let removed = session.request("workspace/symbol", json!({"query":"AUnsaved"}));
    assert_eq!(removed, json!([]));
    assert!(session.request("shutdown", Value::Null).is_null());
    session.notify("exit", Value::Null);
    session.wait_for_exit(0);
}

fn finish(session: &mut Session) {
    session.request("shutdown", Value::Null);
    session.notify("exit", Value::Null);
    session.wait_for_exit(0);
}
fn terminal(session: &mut Session, id: &Value) -> Value {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let job = session.request("penguin/job", json!({"id":id}));
        if matches!(
            job["state"].as_str(),
            Some("succeeded" | "failed" | "cancelled")
        ) {
            return job;
        }
        assert!(Instant::now() < deadline, "job did not finish: {job}");
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn custom_methods_live_details_stale_ids_and_disabled_ai() {
    let mut session = Session::start();
    let init = session.request("initialize", json!({"capabilities":{}}));
    assert_eq!(
        init["capabilities"]["experimental"]["penguin"]["protocolVersion"],
        1
    );
    session.notify("initialized", json!({}));
    let status = session.request("penguin/status", json!({}));
    assert_eq!(status["protocolVersion"], 1);
    assert_eq!(status["state"], "ready");
    let uri = "untitled:Metadata.h";
    session.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"cpp","version":1,
        "text":"/** Actor documentation. */
UCLASS() class AHero : public AActor { UFUNCTION() void Jump(int Count); };"}}),
    );
    let symbols = session.request("penguin/symbols", json!({"query":"AHero","limit":5}));
    assert_eq!(symbols[0]["name"], "AHero");
    assert_eq!(symbols[0]["file"], uri);
    assert_eq!(symbols[0]["line"], 2);
    assert_eq!(symbols[0]["macroName"], "UCLASS");
    assert!(symbols[0]["documentation"]
        .as_str()
        .unwrap()
        .contains("Actor documentation"));
    let id = symbols[0]["id"].clone();
    let detail = session.request("penguin/symbol", json!({"id":id}));
    assert_eq!(detail["name"], "AHero");
    let methods = session.request("penguin/symbols", json!({"query":"Jump"}));
    assert_eq!(methods[0]["owner"], "AHero");
    assert!(methods[0]["signature"].as_str().unwrap().contains("Jump"));
    let inheritance = session.request("penguin/inheritance", json!({"name":"AActor"}));
    assert_eq!(inheritance["derived"][0]["name"], "AHero");
    session.notify("textDocument/didChange", json!({"textDocument":{"uri":uri,"version":2},"contentChanges":[{"text":"UCLASS() class AChanged {};"}]}));
    assert!(session
        .request("penguin/symbol", json!({"id":id}))
        .is_null());
    let id = session.request("penguin/symbols", json!({"query":"AChanged"}))[0]["id"].clone();
    session.notify("textDocument/didClose", json!({"textDocument":{"uri":uri}}));
    session.notify("textDocument/didOpen", json!({"textDocument":{"uri":uri,"languageId":"cpp","version":2,"text":"UCLASS() class AChanged {};"}}));
    assert!(session
        .request("penguin/symbol", json!({"id":id}))
        .is_null());
    assert!(session
        .request("penguin/job", json!({"id":"expired"}))
        .is_null());
    assert_eq!(
        session.request("penguin/cancelJob", json!({"id":"expired"}))["cancelled"],
        false
    );
    assert!(session.response("penguin/reindex", json!({}))["error"].is_object());
    let error = session.response(
        "penguin/ai",
        json!({"task":"explain","source":"int Count;"}),
    );
    assert!(error["error"]["message"]
        .as_str()
        .unwrap()
        .contains("disabled"));
    finish(&mut session);
}

#[test]
fn custom_index_jobs_engine_roots_aliases_and_revision_expiry() {
    use tower_lsp::lsp_types::Url;
    let temp = tempfile::tempdir().unwrap();
    let project = temp.path().join("Project");
    let engine = temp.path().join("Engine");
    std::fs::create_dir_all(&project).unwrap();
    std::fs::create_dir_all(&engine).unwrap();
    let header = project.join("Hero.h");
    std::fs::write(
        &header,
        "/** Disk documentation. */
UCLASS() class AHero {};",
    )
    .unwrap();
    std::fs::write(engine.join("Engine.h"), "UCLASS() class AEngine {};").unwrap();
    let root = Url::from_directory_path(&project).unwrap();
    let mut session = Session::start();
    session.request("initialize", json!({"capabilities":{},"rootUri":root,"initializationOptions":{"penguin":{"adapter":"vscode","engineRoots":[engine]}}}));
    session.notify("initialized", json!({}));
    let job = session.request("penguin/reindex", json!({}));
    assert!(job["id"].is_string());
    assert!(job["sessionId"].is_string());
    let done = terminal(&mut session, &job["id"]);
    assert_eq!(done["state"], "succeeded");
    assert!(done["result"]["indexed"].is_number());
    assert!(done["result"]["unchanged"].is_number());
    let status = session.request("penguin/status", json!({}));
    assert_eq!(status["sessionId"], job["sessionId"]);
    assert_eq!(status["roots"].as_array().unwrap().len(), 2);
    assert_eq!(status["symbols"], 2);
    let symbols = session.request("penguin/symbols", json!({"query":"AHero"}));
    let id = symbols[0]["id"].clone();
    assert_eq!(
        Url::from_file_path(symbols[0]["file"].as_str().unwrap()).unwrap(),
        Url::from_file_path(&header).unwrap()
    );
    let details = session.request("penguin/symbol", json!({"id":id}));
    assert!(details["documentation"]
        .as_str()
        .unwrap()
        .contains("Disk documentation"));
    let header_uri = Url::from_file_path(&header).unwrap();
    session.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":header_uri,"languageId":"cpp","version":1,"text":""}}),
    );
    assert_eq!(
        session.request("penguin/symbols", json!({"query":"AHero"})),
        json!([])
    );
    assert!(session
        .request("penguin/symbol", json!({"id":id}))
        .is_null());
    session.notify(
        "textDocument/didClose",
        json!({"textDocument":{"uri":header_uri}}),
    );
    assert_eq!(
        session.request("penguin/symbols", json!({"query":"AHero"}))[0]["name"],
        "AHero"
    );
    let job = session.request("penguin/reindex", json!({}));
    terminal(&mut session, &job["id"]);
    assert!(session
        .request("penguin/symbol", json!({"id":id}))
        .is_null());
    finish(&mut session);
}

fn fake_ai(
    stall: bool,
) -> (
    String,
    Receiver<()>,
    std::sync::mpsc::Sender<()>,
    std::thread::JoinHandle<()>,
) {
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let endpoint = format!("http://{}", listener.local_addr().unwrap());
    let (seen_tx, seen_rx) = mpsc::channel();
    let (release_tx, release_rx) = mpsc::channel();
    let thread = std::thread::spawn(move || {
        listener.set_nonblocking(true).unwrap();
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut socket = loop {
            if let Ok((socket, _)) = listener.accept() {
                break socket;
            }
            assert!(Instant::now() < deadline, "no local AI connection");
            std::thread::sleep(Duration::from_millis(5));
        };
        socket.set_nonblocking(false).unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        let mut reader = BufReader::new(socket.try_clone().unwrap());
        let mut line = String::new();
        reader.read_line(&mut line).unwrap();
        assert!(line.starts_with("POST /api/generate "), "{line}");
        let mut length = 0;
        loop {
            line.clear();
            reader.read_line(&mut line).unwrap();
            if line == "\r\n" {
                break;
            }
            if let Some(value) = line.to_ascii_lowercase().strip_prefix("content-length:") {
                length = value.trim().parse().unwrap();
            }
        }
        let mut body = vec![0; length];
        reader.read_exact(&mut body).unwrap();
        let payload: Value = serde_json::from_slice(&body).unwrap();
        assert_eq!(payload["stream"], false);
        seen_tx.send(()).unwrap();
        if stall {
            let _ = release_rx.recv_timeout(Duration::from_secs(10));
        }
        let body = r#"{"done":true,"model":"test-model","response":"Preview only","eval_count":3}"#;
        let _ = write!(socket, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}", body.len(), body);
    });
    (endpoint, seen_rx, release_tx, thread)
}

#[test]
fn local_ai_returns_prompt_jobs_remains_responsive_and_cancels_in_both_session_modes() {
    use tower_lsp::lsp_types::Url;
    for disk in [false, true] {
        let (endpoint, seen, release, thread) = fake_ai(true);
        let temp = tempfile::tempdir().unwrap();
        let mut session = Session::start();
        let mut init = json!({"capabilities":{},"initializationOptions":{"penguin":{"ai":{"enabled":true,"endpoint":endpoint,"model":"test-model"}}}});
        if disk {
            init["rootUri"] = json!(Url::from_directory_path(temp.path()).unwrap());
        }
        session.request("initialize", init);
        session.notify("initialized", json!({}));
        let start = Instant::now();
        let job = session.request(
            "penguin/ai",
            json!({"task":"explain","source":"int Count;"}),
        );
        assert!(start.elapsed() < Duration::from_secs(2));
        seen.recv_timeout(Duration::from_secs(5)).unwrap();
        let status = session.request("penguin/status", json!({}));
        assert_eq!(job["sessionId"], status["sessionId"]);
        session.notify("textDocument/didOpen", json!({"textDocument":{"uri":"untitled:Responsive.h","languageId":"cpp","version":1,"text":"UCLASS() class AResponsive {};"}}));
        assert_eq!(
            session.request(
                "textDocument/documentSymbol",
                json!({"textDocument":{"uri":"untitled:Responsive.h"}})
            )[0]["name"],
            "AResponsive"
        );
        let second_job = session.request("penguin/ai", json!({"task":"explain","source":"second"}));
        assert!(
            session.response("penguin/ai", json!({"task":"explain","source":"third"}))["error"]
                .is_object()
        );
        assert_eq!(
            session.request("penguin/cancelJob", json!({"id":second_job["id"]}))["cancelled"],
            true
        );
        assert_eq!(
            session.request("penguin/cancelJob", json!({"id":job["id"]}))["cancelled"],
            true
        );
        assert_eq!(terminal(&mut session, &job["id"])["state"], "cancelled");
        let _ = release.send(());
        thread.join().unwrap();
        assert!(session.request("penguin/job", json!({"id":job["id"]}))["result"].is_null());
        finish(&mut session);
    }
}

#[test]
fn local_ai_success_maps_preview_to_contract_text_and_rejects_remote_endpoints() {
    let (endpoint, seen, _, thread) = fake_ai(false);
    let mut session = Session::start();
    session.request("initialize", json!({"capabilities":{},"initializationOptions":{"penguin":{"ai":{"enabled":true,"endpoint":endpoint}}}}));
    let job = session.request(
        "penguin/ai",
        json!({"task":"generate","source":"","instruction":"propose a function"}),
    );
    seen.recv_timeout(Duration::from_secs(5)).unwrap();
    let result = terminal(&mut session, &job["id"]);
    assert_eq!(result["result"]["text"], "Preview only");
    assert_eq!(result["result"]["model"], "test-model");
    assert!(result["result"].get("preview").is_none());
    thread.join().unwrap();
    finish(&mut session);
    let mut session = Session::start();
    session.request("initialize", json!({"capabilities":{},"initializationOptions":{"penguin":{"ai":{"enabled":true,"endpoint":"https://example.com"}}}}));
    assert!(session.response(
        "penguin/ai",
        json!({"task":"explain","source":"private source"})
    )["error"]
        .is_object());
    finish(&mut session);
}

#[test]
fn diagnostics_only_publish_latest_revision_and_clear_on_close_or_disable() {
    let mut session = Session::start();
    session.request("initialize", json!({"capabilities":{}}));
    let uri = "untitled:Style.h";
    let bad = "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool bReady;";
    session.notify(
        "textDocument/didOpen",
        json!({"textDocument":{"uri":uri,"languageId":"cpp","version":1,"text":bad}}),
    );
    session.notify("textDocument/didChange", json!({"textDocument":{"uri":uri,"version":2},"contentChanges":[{"text":"UPROPERTY() bool bReady;"}]}));
    session.request("penguin/status", json!({}));
    std::thread::sleep(Duration::from_millis(250));
    session.request("penguin/status", json!({}));
    let diagnostics: Vec<_> = session
        .notifications
        .iter()
        .filter(|n| n["method"] == "textDocument/publishDiagnostics")
        .collect();
    assert!(!diagnostics.is_empty());
    assert!(
        diagnostics
            .iter()
            .all(|n| n["params"]["version"] == 2 && n["params"]["diagnostics"] == json!([])),
        "{diagnostics:?}"
    );
    session.notifications.clear();
    session.notify(
        "textDocument/didChange",
        json!({"textDocument":{"uri":uri,"version":3},"contentChanges":[{"text":bad}]}),
    );
    session.request("penguin/status", json!({}));
    std::thread::sleep(Duration::from_millis(250));
    session.request("penguin/status", json!({}));
    assert!(session
        .notifications
        .iter()
        .any(|n| n["params"]["version"] == 3
            && n["params"]["diagnostics"]
                .as_array()
                .is_some_and(|d| d.len() == 1)));
    session.notifications.clear();
    session.notify(
        "workspace/didChangeConfiguration",
        json!({"settings":{"penguin":{"style":{"enabled":false}}}}),
    );
    session.request("penguin/status", json!({}));
    std::thread::sleep(Duration::from_millis(200));
    session.request("penguin/status", json!({}));
    assert!(session
        .notifications
        .iter()
        .any(|n| n["params"]["diagnostics"] == json!([])));
    assert!(session
        .notifications
        .iter()
        .filter(|n| n["method"] == "textDocument/publishDiagnostics")
        .all(|n| n["params"]["diagnostics"] == json!([])));
    session.notifications.clear();
    session.notify(
        "workspace/didChangeConfiguration",
        json!({"settings":{"penguin":{"style":{"enabled":true}}}}),
    );
    session.notify("textDocument/didClose", json!({"textDocument":{"uri":uri}}));
    session.request("penguin/status", json!({}));
    std::thread::sleep(Duration::from_millis(250));
    session.request("penguin/status", json!({}));
    assert!(session
        .notifications
        .iter()
        .filter(|n| n["method"] == "textDocument/publishDiagnostics")
        .all(|n| n["params"]["diagnostics"] == json!([])));
    finish(&mut session);
}

#[test]
fn shutdown_cancels_active_ai_without_waiting_for_http_response() {
    use tower_lsp::lsp_types::Url;
    for disk in [false, true] {
        let (endpoint, seen, release, thread) = fake_ai(true);
        let temp = tempfile::tempdir().unwrap();
        let mut session = Session::start();
        let mut init = json!({"capabilities":{},"initializationOptions":{"penguin":{"ai":{"enabled":true,"endpoint":endpoint}}}});
        if disk {
            init["rootUri"] = json!(Url::from_directory_path(temp.path()).unwrap());
        }
        session.request("initialize", init);
        session.notify("initialized", json!({}));
        session.request(
            "penguin/ai",
            json!({"task":"explain","source":"int Count;"}),
        );
        seen.recv_timeout(Duration::from_secs(5)).unwrap();
        let start = Instant::now();
        finish(&mut session);
        assert!(
            start.elapsed() < Duration::from_secs(3),
            "shutdown waited for HTTP inference"
        );
        let _ = release.send(());
        thread.join().unwrap();
    }
}

#[test]
fn second_identical_namespace_is_unavailable_without_starting_legacy_writer() {
    use tower_lsp::lsp_types::Url;
    let temp = tempfile::tempdir().unwrap();
    let init = json!({"capabilities":{},"rootUri":Url::from_directory_path(temp.path()).unwrap()});
    let mut first = Session::start();
    first.request("initialize", init.clone());
    let mut second = Session::start();
    second.request("initialize", init);
    let status = second.request("penguin/status", json!({}));
    assert_eq!(status["state"], "unavailable");
    assert!(status["message"]
        .as_str()
        .unwrap()
        .contains("active writer"));
    assert!(second.response("penguin/reindex", json!({}))["error"].is_object());
    second.notify("textDocument/didOpen", json!({"textDocument":{"uri":"untitled:Memory.h","languageId":"cpp","version":1,"text":"UCLASS() class AMemory {};"}}));
    assert_eq!(
        second.request("penguin/symbols", json!({"query":"AMemory"}))[0]["name"],
        "AMemory"
    );
    assert!(!temp
        .path()
        .join(".vs/PenguinExtension/penguin_core.db")
        .exists());
    assert!(!temp
        .path()
        .join(".vs/PenguinExtension/penguin_cache.db")
        .exists());
    finish(&mut second);
    finish(&mut first);
}
