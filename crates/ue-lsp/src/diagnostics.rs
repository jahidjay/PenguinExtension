//! Pure conversion of conservative style observations to UTF-16 LSP ranges.
use crate::{docs::Document, extensions::StyleOptions};
use tower_lsp::lsp_types::{Diagnostic, DiagnosticSeverity, NumberOrString, Range};

pub(crate) fn check(document: &Document, options: &StyleOptions) -> Vec<Diagnostic> {
    if !options.enabled {
        return Vec::new();
    }
    ue_style::check(&document.text, &document.parsed, &options.config())
        .into_iter()
        .take(500)
        .map(|item| Diagnostic {
            range: Range::new(
                document
                    .lines
                    .position(&document.text, item.byte_range.start),
                document.lines.position(&document.text, item.byte_range.end),
            ),
            severity: Some(match item.severity {
                ue_style::Severity::Warning => DiagnosticSeverity::WARNING,
                ue_style::Severity::Hint => DiagnosticSeverity::HINT,
            }),
            code: Some(NumberOrString::String(item.code.into())),
            source: Some("penguin-style".into()),
            message: item.message,
            ..Default::default()
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn offsets_after_non_bmp_text_are_utf16() {
        let doc = Document::parse(
            "/* 🐧 */ UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool bReady;".into(),
        );
        let diagnostics = check(&doc, &StyleOptions::default());
        assert_eq!(diagnostics.len(), 1);
        assert_eq!(diagnostics[0].range.start.character, 9);
        assert_eq!(diagnostics[0].source.as_deref(), Some("penguin-style"));
        assert_eq!(diagnostics[0].severity, Some(DiagnosticSeverity::WARNING));
    }
}
