use std::future::Future;
use std::pin::Pin;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::task::{Context, Poll};

use tower::Service;
use tower_lsp::jsonrpc::{Error, Request, Response};
use tower_lsp::{ExitedError, LspService, Server};
use ue_lsp::server::Backend;

/// Owns process lifetime without replacing tower-lsp's framing or routing.
struct ProcessService {
    inner: LspService<Backend>,
    shutdown_completed: Arc<AtomicBool>,
}

impl Service<Request> for ProcessService {
    type Response = Option<Response>;
    type Error = ExitedError;
    type Future = Pin<Box<dyn Future<Output = Result<Self::Response, Self::Error>> + Send>>;

    fn poll_ready(&mut self, cx: &mut Context<'_>) -> Poll<Result<(), Self::Error>> {
        self.inner.poll_ready(cx)
    }

    fn call(&mut self, request: Request) -> Self::Future {
        let shutdown_completed = self.shutdown_completed.clone();
        if request.method() == "exit" {
            // exit is a parameterless notification, not a request. Do not let
            // malformed calls reach tower-lsp's unconditional ExitService.
            if request.id().is_some() || request.params().is_some() {
                let response = request
                    .id()
                    .cloned()
                    .map(|id| Response::from_error(id, Error::invalid_request()));
                return Box::pin(async move { Ok(response) });
            }
            return Box::pin(async move {
                // Server calls services while reading ahead, but polls their
                // futures sequentially at concurrency_level(1). Exit here, not
                // in call(), so any preceding shutdown drains Backend's workers
                // before we inspect its result. Delegating exit would mark the
                // inner service exited immediately and cancel pending shutdown.
                let code = if shutdown_completed.load(Ordering::Acquire) {
                    0
                } else {
                    1
                };
                // tower-lsp 0.20 may wait for another input frame after exit.
                // Returning from main also waits for Tokio's blocking stdin
                // read, so an open client pipe requires actual process exit.
                std::process::exit(code);
            });
        }

        let is_shutdown = request.method() == "shutdown" && request.id().is_some();
        let future = self.inner.call(request);
        Box::pin(async move {
            let response = future.await?;
            if is_shutdown && response.as_ref().is_some_and(Response::is_ok) {
                shutdown_completed.store(true, Ordering::Release);
            }
            Ok(response)
        })
    }
}

#[tokio::main]
async fn main() {
    let (service, socket) = ue_lsp::server::service();
    let service = ProcessService {
        inner: service,
        shutdown_completed: Arc::new(AtomicBool::new(false)),
    };
    Server::new(tokio::io::stdin(), tokio::io::stdout(), socket)
        // Preserve wire ordering between buffer notifications and requests.
        // CPU/IO runs on blocking workers; indexing runs in its own task.
        // Sequential dispatch trades request cancellation and parallel requests
        // for a simple, correct revision boundary.
        .concurrency_level(1)
        .serve(service)
        .await;
}
