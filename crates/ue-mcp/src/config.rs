//! Launch-only scope and local inference configuration. No tool can change these.
use std::{ffi::OsString, path::Path};
use ue_ai::AiConfig;

pub const HELP: &str =
    "penguin-mcp --root <absolute-directory> [--root <absolute-directory> ...]\n\
    [--engine-root <absolute-directory>] [--ai [--model <model>] [--endpoint <loopback-url>]]\n\
\nMCP newline-delimited JSON on stdin/stdout (not LSP Content-Length).\n\
AI is OFF by default. --ai explicitly enables local inference using an already-running\n\
Ollama service; default http://127.0.0.1:11434, model qwen2.5-coder:3b.\n\
No model downloads, service launches, source writes, or execution.\n";

#[derive(Debug, Clone)]
pub struct LaunchConfig {
    pub roots: Vec<String>,
    pub ai: Option<AiConfig>,
}

#[derive(Debug)]
pub enum Command {
    Run(LaunchConfig),
    Help,
    Version,
}

impl LaunchConfig {
    /// Parse arguments excluding `argv[0]`. Disk validation belongs to core's worker.
    pub fn parse(args: impl IntoIterator<Item = OsString>) -> Result<Command, String> {
        let mut args = args.into_iter();
        let mut roots = Vec::new();
        let mut engine = None;
        let mut ai = false;
        let mut model = None;
        let mut endpoint = None;
        while let Some(arg) = args.next() {
            let arg = arg.into_string().map_err(|_| "arguments must be UTF-8")?;
            if arg == "--help" || arg == "-h" {
                return Ok(Command::Help);
            }
            if arg == "--version" || arg == "-V" {
                return Ok(Command::Version);
            }
            if arg == "--ai" {
                if ai {
                    return Err("--ai may only appear once".into());
                }
                ai = true;
                continue;
            }
            if !matches!(
                arg.as_str(),
                "--root" | "--engine-root" | "--model" | "--endpoint"
            ) {
                return Err(format!("unknown argument {arg}; use --help"));
            }
            let value = args
                .next()
                .ok_or_else(|| format!("{arg} requires a value"))?
                .into_string()
                .map_err(|_| "arguments must be UTF-8")?;
            if value.is_empty() || value.starts_with("--") || value.contains('\0') {
                return Err(format!("{arg} requires a nonempty value"));
            }
            match arg.as_str() {
                "--root" | "--engine-root" => {
                    if value.len() > ue_core::MAX_PATH_BYTES || !Path::new(&value).is_absolute() {
                        return Err(format!("{arg} requires a bounded absolute directory path"));
                    }
                    if arg == "--root" {
                        roots.push(value);
                    } else if engine.replace(value).is_some() {
                        return Err("--engine-root may only appear once".into());
                    }
                }
                "--model" => {
                    if model.replace(value).is_some() {
                        return Err("--model may only appear once".into());
                    }
                }
                "--endpoint" => {
                    if endpoint.replace(value).is_some() {
                        return Err("--endpoint may only appear once".into());
                    }
                }
                _ => unreachable!(),
            }
        }
        if roots.is_empty() {
            return Err("at least one --root is required".into());
        }
        if let Some(engine) = engine {
            roots.push(engine);
        }
        if roots.len() > ue_core::MAX_ROOTS {
            return Err("at most 16 roots including engine-root are allowed".into());
        }
        if !ai && (model.is_some() || endpoint.is_some()) {
            return Err("--model and --endpoint require explicit --ai".into());
        }
        let ai = if ai {
            let mut config = AiConfig::default();
            if let Some(model) = model {
                config.model = model;
            }
            if let Some(endpoint) = endpoint {
                config.endpoint = endpoint;
            }
            Some(config)
        } else {
            None
        };
        Ok(Command::Run(Self { roots, ai }))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn parse(args: &[&str]) -> Result<Command, String> {
        LaunchConfig::parse(args.iter().map(OsString::from))
    }
    fn root() -> &'static str {
        if cfg!(windows) {
            r"C:\project"
        } else {
            "/project"
        }
    }
    #[test]
    fn explicit_launch_scope_and_ai() {
        assert!(parse(&[]).is_err());
        assert!(parse(&["--root", "relative"]).is_err());
        assert!(parse(&["--root", root(), "--model", "x"]).is_err());
        assert!(parse(&["--root", root(), "--endpoint", "http://127.0.0.1:1"]).is_err());
        let Command::Run(c) = parse(&["--root", root()]).unwrap() else {
            panic!()
        };
        assert!(c.ai.is_none());
        let Command::Run(c) = parse(&[
            "--root",
            root(),
            "--root",
            root(),
            "--engine-root",
            root(),
            "--ai",
        ])
        .unwrap() else {
            panic!()
        };
        assert_eq!(c.roots.len(), 3);
        assert_eq!(c.ai.unwrap().model, "qwen2.5-coder:3b");
    }
    #[test]
    fn reject_unknown_missing_and_duplicate_flags() {
        for extra in [
            vec!["--wat"],
            vec!["--root"],
            vec!["--ai", "--ai"],
            vec!["--engine-root", root(), "--engine-root", root()],
        ] {
            let mut args = vec!["--root", root()];
            args.extend(extra);
            assert!(parse(&args).is_err());
        }
        assert!(matches!(parse(&["--help"]), Ok(Command::Help)));
    }
}
