# ue-ai

Bounded, local-only Ollama inference for Unreal Engine C++ explanations and generated **previews**. Rust 2021; run async calls inside Tokio with I/O and time enabled.

## API

```rust
use ue_ai::{AiClient, AiConfig, AiError, AiRequest, AiTask};

async fn explain(source: String) -> Result<String, AiError> {
    let client = AiClient::new(AiConfig::default())?;
    let response = client.generate(AiRequest {
        task: AiTask::Explain, // or AiTask::Generate
        source,
        instruction: "Explain the Unreal reflection behavior.".into(),
    }).await?;
    Ok(response.preview)
}
```

- `AiClient::new(config: AiConfig) -> Result<AiClient, AiError>` validates configuration without contacting Ollama. Client is cloneable, `Send + Sync`.
- `AiClient::generate(&self, request: AiRequest) -> impl Future<Output = Result<AiResponse, AiError>>` is `Send`, cancellation-compatible, and performs one nonstreaming `POST /api/generate`.
- `AiRequest { task: AiTask, source: String, instruction: String }`; task serializes as `explain` or `generate`. Missing instruction defaults to empty.
- `AiResponse { preview: String, model: String }` contains raw untrusted preview text and the returned model name.
- `AiConfig` supports `Default`, Serde, and partial JSON such as `{ "max_output_tokens": 512 }`. Unknown config/request fields are rejected. Deserialize does not validate numeric ranges or endpoints; `new` always does.
- `AiError` implements `std::error::Error`, `Display`, `Clone`, `PartialEq`, and `Eq`. Variants: `InvalidConfig { field, reason }`, `InputTooLarge { field, limit }`, `PromptTooLarge { limit }`, `ResponseTooLarge { limit }`, `OutputTokenLimitExceeded { limit }`, `ClientInitialization`, `ServiceUnavailable`, `ModelNotFound { model }`, `RedirectRejected { status }`, `HttpStatus { status }`, `Timeout`, `Transport`, `InvalidResponse { reason }`, `ModelError`. Fields/reasons are static strings, byte limits are `usize`, token limits are `u32`, statuses are `u16`.

## Defaults and hard limits

| Config field | Type | Default | Accepted range / ceiling |
| --- | --- | --- | --- |
| `endpoint` | `String` | `http://127.0.0.1:11434` | HTTP loopback origin only, at most 256 ASCII bytes |
| `model` | `String` | `qwen2.5-coder:3b` | 1–128 ASCII bytes, starts alphanumeric; letters/digits or `_`, `-`, `.`, `/`, `:` |
| `max_source_bytes` | `usize` | 65,536 | 1–262,144 |
| `max_instruction_bytes` | `usize` | 8,192 | 1–16,384 |
| `max_prompt_bytes` | `usize` | 131,072 | 1–1,048,576 |
| `max_response_bytes` | `usize` | 262,144 | 1–2,097,152 |
| `max_output_tokens` | `u32` | 1,024 | 1–4,096; less than context window |
| `context_window_tokens` | `u32` | 8,192 | 256–16,384 |
| `timeout_ms` | `u64` | 60,000 | 1–120,000 |

Byte counts are UTF-8, not characters. Input is rejected, never silently truncated. The prompt limit covers the **entire serialized HTTP body**, including system text and double JSON escaping. Response-body bytes are capped during reading, including chunked and error responses; oversized Content-Length is rejected early. Encoded/compressed bodies are rejected. Reqwest/Hyper separately imposes HTTP protocol/header parsing limits. The overall HTTP deadline includes connection, headers, and the complete body, not a resettable idle timeout.

`num_predict` and `num_ctx` are supplied to Ollama; reported output-token overruns are rejected. Exact token counts cannot be verified independently without the model tokenizer, and Ollama may truncate context to its configured window. The response byte bound remains enforced even if token metadata is absent or dishonest. `keep_alive: 0` avoids retaining a model loaded for this request indefinitely.

## Safety and cancellation

Only canonical IPv4 loopback literals (`127.0.0.0/8`), bracketed IPv6 loopback (`::1`), and exact lowercase `localhost` are allowed, optionally with a port and one trailing slash. Localhost resolution is pinned to IPv4/IPv6 loopback, bypassing DNS and the hosts file. Credentials, query/fragment, non-root paths, escaped hosts, alternative numeric IPv4 forms, IPv6 zone IDs/mapped addresses, backslashes, controls, whitespace, and remote addresses are rejected **before URL normalization**. HTTPS is intentionally not supported. Environment/system proxies, redirects, retries, and idle connection pooling are disabled.

Dropping an in-flight future (or aborting the caller-owned task) closes local request I/O. An unpolled future makes no network request. Already-accepted inference may continue server-side; cancellation does not claim to interrupt the model itself. `ue-core` owns admission, concurrency, jobs, and cancellation. The crate spawns no inference jobs.

Source/instruction are JSON-framed data; output is always data. Prompt wording is not a security boundary. Render previews as text (escape HTML), require explicit user review before applying anything, and never execute model output automatically. This crate does not access source files, apply edits, execute commands, pull models, or start Ollama. Start the service and install the configured model separately. Error messages never echo prompt/source or arbitrary server error text. Loopback restriction is not authentication of the local process listening at the chosen port.

## Checks

```sh
cargo test -p ue-ai
cargo fmt -p ue-ai -- --check
cargo clippy -p ue-ai --all-targets --no-deps -- -D warnings
```

Tests use ephemeral loopback TCP mock servers only; no live Ollama calls, model downloads, or internet inference. Includes successes, Serde/defaults, endpoint attacks, limits, malformed/oversized/chunked responses, service/model failures, redirect rejection, proxy isolation, IPv6/localhost, deadlines, and dropped-future cancellation.
