use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{Read, Write},
    path::Path,
};

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields, default)]
pub struct Settings {
    pub ai_enabled: bool,
    pub endpoint: String,
    pub model: String,
    pub naming_checks: bool,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            ai_enabled: false,
            endpoint: "http://127.0.0.1:11434".into(),
            model: "qwen2.5-coder:3b".into(),
            naming_checks: false,
        }
    }
}
impl Settings {
    pub fn ai_config(&self) -> ue_ai::AiConfig {
        ue_ai::AiConfig {
            endpoint: self.endpoint.clone(),
            model: self.model.clone(),
            max_source_bytes: 32768,
            max_instruction_bytes: 2048,
            max_response_bytes: 256 * 1024,
            ..Default::default()
        }
    }
    pub fn validate(&self) -> Result<(), String> {
        if self.endpoint.len() > 512 || self.model.len() > 128 {
            return Err("Settings exceed length limits".into());
        }
        // AiClient construction validates loopback, limits and model but sends no HTTP.
        ue_ai::AiClient::new(self.ai_config())
            .map(|_| ())
            .map_err(|e| e.to_string())
    }
}
pub fn load(path: &Path) -> Result<Settings, String> {
    let file = match fs::File::open(path) {
        Ok(f) => f,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Settings::default()),
        Err(e) => return Err(e.to_string()),
    };
    let mut bytes = Vec::new();
    file.take(8193)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() > 8192 {
        return Err("Settings file exceeds 8 KiB".into());
    }
    let settings: Settings =
        serde_json::from_slice(&bytes).map_err(|e| format!("Invalid local settings: {e}"))?;
    settings.validate()?;
    Ok(settings)
}
pub fn save(path: &Path, settings: &Settings) -> Result<(), String> {
    settings.validate()?;
    let parent = path.parent().ok_or("Missing app configuration directory")?;
    fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    let mut file = tempfile::NamedTempFile::new_in(parent).map_err(|e| e.to_string())?;
    serde_json::to_writer_pretty(&mut file, settings).map_err(|e| e.to_string())?;
    file.write_all(
        b"
",
    )
    .map_err(|e| e.to_string())?;
    file.as_file().sync_all().map_err(|e| e.to_string())?;
    file.persist(path).map_err(|e| e.to_string())?;
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn off_by_default_and_atomic_replacement() {
        let d = tempfile::tempdir().unwrap();
        let p = d.path().join("settings.json");
        assert!(!load(&p).unwrap().ai_enabled);
        save(&p, &Settings::default()).unwrap();
        let s = Settings {
            naming_checks: true,
            ..Default::default()
        };
        save(&p, &s).unwrap();
        assert_eq!(load(&p).unwrap(), s);
        assert_eq!(fs::read_dir(d.path()).unwrap().count(), 1);
    }
    #[test]
    fn reject_remote_redirectable_or_credential_endpoints() {
        for endpoint in [
            "https://example.com",
            "http://127.0.0.1.evil.test",
            "http://user@localhost:11434",
            "http://localhost:11434/api",
            "http://192.168.1.1",
        ] {
            assert!(
                Settings {
                    endpoint: endpoint.into(),
                    ..Default::default()
                }
                .validate()
                .is_err(),
                "{endpoint}"
            );
        }
    }
    #[test]
    fn reject_corrupt_or_unknown_settings() {
        let d = tempfile::tempdir().unwrap();
        let p = d.path().join("settings.json");
        for data in ["not json", "{\"shell\":true}"] {
            fs::write(&p, data).unwrap();
            assert!(load(&p).is_err());
        }
    }
}
