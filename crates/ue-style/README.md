# ue-style

Conservative, source-only checks for reflected Unreal declarations. This library does not perform file I/O, index a workspace, expand macros, resolve C++ types, or apply fixes.

## API

```rust
use ue_parser::UnrealParser;
use ue_style::{check, StyleConfig};

let source = "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool bReady;";
let parsed = UnrealParser::new().unwrap().parse(source);
let diagnostics = check(source, &parsed, &StyleConfig::default());
assert_eq!(diagnostics.len(), 1);
```

Pass a parse of the exact same source snapshot. Diagnostics contain a stable code, severity, explanation, and UTF-8 byte range; editor adapters must convert ranges to their own coordinate system. They are not UTF-16 positions. Contradiction diagnostics cover the reflection macro, while naming hints cover the identifier.

## Rules

| Code | Default | Meaning |
| --- | --- | --- |
| `UE_STYLE_001` | Warning, on | Both bare `BlueprintReadOnly` and `BlueprintReadWrite` specifiers |
| `UE_STYLE_002` | Warning, on | More than one distinct edit/visibility mode |
| `UE_STYLE_101` | Hint, opt-in | A reflected struct name lacks `F` and a suffix |
| `UE_STYLE_102` | Hint, opt-in | A reflected enum name lacks `E` and a suffix |
| `UE_STYLE_103` | Hint, opt-in | A provable scalar `bool` property lacks the `bUppercase` convention |

The library's `StyleConfig` JSON keys are `blueprint_access_conflicts`, `edit_visibility_conflicts`, `struct_prefix`, `enum_prefix`, and `bool_property_prefix`. Missing keys use defaults. Adapters can provide their own setting names, but must map them explicitly.

The lexer verifies source evidence instead of trusting a substring or a parser's permissive macro match. Comments, strings, directive definitions, nested metadata, unknown wrappers, and malformed syntax are suppressed when evidence is uncertain. Naming hints require an adjacent simple declaration; aliases, inferred types, and arbitrary C++ expressions are deliberately not resolved. This is not UnrealHeaderTool or compiler validation, and disabled preprocessor branches are not evaluated.

## Verification

```sh
cargo test -p ue-style
cargo clippy -p ue-style --all-targets --no-deps -- -D warnings
cargo fmt -p ue-style -- --check
```

Tests cover rule configuration, false positives, incomplete headers, UTF-8/CRLF ranges, stale parse ranges, and deterministic ordering. They do not require an Unreal installation.
