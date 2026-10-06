use std::process::ExitCode;
use tokio_util::sync::CancellationToken;
use ue_mcp::config::{Command, LaunchConfig, HELP};
fn main() -> ExitCode {
    let command = match LaunchConfig::parse(std::env::args_os().skip(1)) {
        Ok(command) => command,
        Err(error) => {
            eprintln!("penguin-mcp: {error}");
            return ExitCode::from(2);
        }
    };
    let config = match command {
        Command::Help => {
            eprint!("{HELP}");
            return ExitCode::SUCCESS;
        }
        Command::Version => {
            eprintln!("penguin-mcp {}", env!("CARGO_PKG_VERSION"));
            return ExitCode::SUCCESS;
        }
        Command::Run(config) => config,
    };
    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .max_blocking_threads(8)
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(error) => {
            eprintln!("penguin-mcp: {error}");
            return ExitCode::FAILURE;
        }
    };
    let result = runtime.block_on(async move {
        let cancel = CancellationToken::new();
        let signal_cancel = cancel.clone();
        let signal = tokio::spawn(async move {
            if tokio::signal::ctrl_c().await.is_ok() {
                signal_cancel.cancel();
            }
        });
        let result = ue_mcp::serve(config, tokio::io::stdin(), tokio::io::stdout(), cancel).await;
        signal.abort();
        result
    });
    // Tokio stdin cannot be interrupted on every OS. Core and AI are drained first.
    runtime.shutdown_timeout(std::time::Duration::from_secs(2));
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("penguin-mcp: {error}");
            ExitCode::FAILURE
        }
    }
}
