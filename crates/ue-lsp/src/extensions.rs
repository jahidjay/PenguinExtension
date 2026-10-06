//! The thin-client wire contract. Core DTOs deliberately stay behind this map.
use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Default, Deserialize)]
pub struct EmptyParams {}

#[derive(Debug, Deserialize)]
pub struct SearchParams {
    pub query: String,
    pub limit: Option<usize>,
}
#[derive(Debug, Deserialize)]
pub struct IdParams {
    pub id: String,
}
#[derive(Debug, Deserialize)]
pub struct NameParams {
    pub name: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Symbol {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub macro_name: String,
    pub file: String,
    pub line: usize,
    pub type_name: Option<String>,
    pub specifiers: Vec<ue_core::SpecifierDto>,
    pub bases: Vec<String>,
    pub signature: Option<String>,
    pub documentation: Option<String>,
    pub owner: Option<String>,
    pub qualified_name: Option<String>,
}
impl Symbol {
    pub(crate) fn live(
        symbol: &ue_parser::UnrealSymbol,
        metadata: Option<&ue_parser::SymbolMetadata>,
        file: String,
    ) -> Self {
        Self {
            id: String::new(),
            name: symbol.name.clone(),
            kind: symbol.kind.as_str().into(),
            macro_name: symbol.macro_name.clone(),
            file,
            line: symbol.line,
            type_name: symbol.type_name.clone(),
            specifiers: symbol
                .specifiers
                .iter()
                .map(|s| ue_core::SpecifierDto {
                    key: s.key.clone(),
                    value: s.value.clone(),
                })
                .collect(),
            bases: symbol.bases.clone(),
            signature: metadata.and_then(|m| m.signature.clone()),
            documentation: metadata.and_then(|m| m.documentation.clone()),
            owner: metadata.and_then(|m| m.owner.clone()),
            qualified_name: metadata.and_then(|m| m.qualified_name.clone()),
        }
    }
    pub(crate) fn disk(symbol: ue_core::SymbolDto, file: String) -> Self {
        Self {
            id: String::new(),
            name: symbol.name,
            kind: symbol.kind,
            macro_name: symbol.macro_name,
            file,
            line: symbol.line,
            type_name: symbol.type_name,
            specifiers: symbol.specifiers,
            bases: symbol.bases,
            signature: None,
            documentation: None,
            owner: None,
            qualified_name: None,
        }
    }
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Status {
    pub protocol_version: u32,
    pub session_id: String,
    pub state: &'static str,
    pub roots: Vec<String>,
    pub files: usize,
    pub symbols: usize,
    pub message: Option<String>,
}
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Job {
    pub id: String,
    pub session_id: String,
    pub kind: String,
    pub state: ue_core::JobState,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}
impl From<ue_core::JobStatus> for Job {
    fn from(value: ue_core::JobStatus) -> Self {
        Self {
            id: value.job_id,
            session_id: value.session_id,
            kind: value.kind.clone(),
            state: value.state,
            result: value.result.map(|mut result| {
                if value.kind.starts_with("index") {
                    if let Some(report) = result.get_mut("report") {
                        return report.take();
                    }
                }
                result
            }),
            error: value.error.map(|e| e.message),
        }
    }
}
#[derive(Debug, Serialize)]
pub struct Inheritance {
    pub bases: Vec<String>,
    pub derived: Vec<Symbol>,
}
#[derive(Debug, Serialize)]
pub struct CancelResult {
    pub cancelled: bool,
}

/// Accept the public camelCase style configuration without coupling the wire
/// format to the style library's internal serde spelling. Unknown future keys
/// are ignored, like other LSP initialization options.
#[derive(Debug, Clone, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub(crate) struct StyleOptions {
    pub enabled: bool,
    #[serde(alias = "blueprint_access_conflicts")]
    pub blueprint_access_conflicts: bool,
    #[serde(alias = "edit_visibility_conflicts")]
    pub edit_visibility_conflicts: bool,
    #[serde(alias = "struct_prefix")]
    pub struct_prefix: bool,
    #[serde(alias = "enum_prefix")]
    pub enum_prefix: bool,
    #[serde(alias = "bool_property_prefix")]
    pub bool_property_prefix: bool,
}
impl Default for StyleOptions {
    fn default() -> Self {
        Self {
            enabled: true,
            blueprint_access_conflicts: true,
            edit_visibility_conflicts: true,
            struct_prefix: false,
            enum_prefix: false,
            bool_property_prefix: false,
        }
    }
}
impl StyleOptions {
    pub fn config(&self) -> ue_style::StyleConfig {
        ue_style::StyleConfig {
            blueprint_access_conflicts: self.blueprint_access_conflicts,
            edit_visibility_conflicts: self.edit_visibility_conflicts,
            struct_prefix: self.struct_prefix,
            enum_prefix: self.enum_prefix,
            bool_property_prefix: self.bool_property_prefix,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn style_wire_defaults_and_camel_case_switches() {
        let options: StyleOptions =
            serde_json::from_value(serde_json::json!({"structPrefix":true,"enabled":false}))
                .unwrap();
        assert!(!options.enabled);
        assert!(options.config().struct_prefix);
        assert!(options.config().blueprint_access_conflicts);
    }
}
