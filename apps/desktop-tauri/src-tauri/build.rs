fn main() {
    static COMMANDS: &[&str] = &[
        "desktop_open",
        "desktop_close",
        "desktop_status",
        "desktop_search",
        "desktop_symbol",
        "desktop_inheritance",
        "desktop_reindex",
        "desktop_job",
        "desktop_cancel",
        "desktop_style",
        "desktop_ai",
        "desktop_settings",
        "desktop_save_settings",
    ];
    tauri_build::try_build(
        tauri_build::Attributes::new()
            .app_manifest(tauri_build::AppManifest::new().commands(COMMANDS)),
    )
    .expect("Tauri build failed");
}
