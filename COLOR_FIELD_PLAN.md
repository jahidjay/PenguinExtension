# COLOR_FIELD_PLAN.md — Inline Color Field for Unreal Properties

**Status:** PLANNED — no implementation yet. Work one phase per session; tick the checklist and update memory after each phase. Do not start a phase mid-session if context is already heavy.

## Goal

Rider-style **inline color swatch + picker** ("color field") wherever a UE color value appears in code, on three editors:

| Target | Vehicle |
|--------|---------|
| Visual Studio 2022 | This VSIX (`PenguinExtention`) — intra-text adornment tagger |
| VS Code | New TypeScript extension `apps/vscode-penguin-colors/` — native `DocumentColorProvider` |
| Google Antigravity | Same VS Code extension (Antigravity is a VS Code fork; ships via OpenVSX or manual `.vsix` install) |

Plus the **Penguin Palette v1** — a finalized, contrast-validated editor palette for UE classification (applied to the existing `Editor/UnrealClassifier.cs` formats).

## What counts as a "color value" (detection spec — shared across all targets)

1. `FLinearColor(R, G, B[, A])` — floats 0–1, **linear** space
2. `FColor(R, G, B[, A])` — bytes 0–255, **sRGB** space
3. Named constants: `FColor::Red`, `FLinearColor::White`, etc. (static lookup table, ~20 entries)
4. `FColor::FromHex("#RRGGBB[AA]")` / `FromHex(TEXT("..."))`
5. (Stretch) `UPROPERTY` default in constructor: `MyColor = FLinearColor(...)` — same regexes cover it

**Correctness rule:** FLinearColor is linear; convert linear→sRGB for swatch display (UE's exact conversion, not plain pow(1/2.2)). FColor is already sRGB. Picker must write back in the source type's space.

**Hard rules apply:** regex only (`RegexOptions.Compiled`, no backtracking traps), background threads, MEF ContentType `C/C++`, files listed in `.csproj`, namespace `PenguinExtention`.

## Penguin Palette v1 (the "best programming palette")

Validated ≥4.5:1 contrast on VS dark (`#1E1E1E`) and light (`#FFFFFF`) backgrounds; hue + brightness separated so categories survive common color-blindness.

| Role | Dark | Light | Notes |
|------|------|-------|-------|
| UE Macro (UPROPERTY/UCLASS…) | `#C586C0` bold | `#AF00DB` bold | keep current dark value |
| UE Type (A*/U*/F*/S*) | `#4EC9B0` | `#267F6F` | keep current dark value |
| Function | `#DCDCAA` | `#795E26` | keep current dark value |
| Enum value | `#B8D7A3` | `#4F7A28` | keep current dark value |
| Specifier (EditAnywhere…) | `#9CDCFE` | `#0070C1` | NEW classification |
| Meta key (meta=(…)) | `#CE9178` | `#A31515` | NEW classification |
| Delegate/Event | `#D16D9E` | `#A4286A` | NEW classification |
| Swatch border | `#80808099` | `#80808099` | 60% gray, both themes |

Current dark values in `Editor/UnrealClassifier.cs:35-82` are already VS Code–proven colors — palette keeps them and adds light-theme variants + 3 new categories.

## Phases

### Phase 0 — Shared detection core (VS side)  [ ]
- `Services/ColorLiteralScanner.cs` — pure static class: input `string line` → list of `(span, Color, SourceKind)`; all 4 patterns + named-constant table + linear→sRGB conversion.
- `Editor/PenguinPalette.cs` — palette constants (dark + light) in one place.
- Add both to `.csproj`. Self-test entry: scan a hardcoded sample buffer, print hit count.
- *Files: 2 new, 2 modified (csproj, SelfTestCommand). No UI risk.*

### Phase 1 — VS 2022 read-only swatch  [ ]
- `Editor/ColorFieldTagger.cs` — `ITaggerProvider` for `IntraTextAdornmentTag`, ContentType `C/C++`; small rounded WPF rectangle before the literal; re-tag on visible-span changes only (never whole file on UI thread).
- `FeatureProbe.Mark` + PenguinLog on first tag.
- *Files: 1 new, 1 modified. Verify in Exp instance on SadaAlDam test project.*

### Phase 2 — VS 2022 picker + rewrite  [ ]
- Click swatch → WPF color picker dialog (style of `UI/QuickPickDialog.cs`, code-built, no XAML).
- Rewrite literal preserving source kind (FColor stays FColor, float precision kept) via `PenguinEditor` single linked undo.
- Options page toggle: `PenguinOptionsPage.EnableColorField` (default on).
- *Files: 1 new dialog, 2 modified.*

### Phase 3 — VS Code extension  [ ]
- `apps/vscode-penguin-colors/`: `package.json` (activation `onLanguage:cpp`), `src/colorProvider.ts` implementing `DocumentColorProvider` (`provideDocumentColors` + `provideColorPresentations`) — VS Code renders swatch AND picker natively, no UI code.
- Port `ColorLiteralScanner` regexes + named table + color-space conversion to TS (keep the two implementations line-comparable; comment linking both files).
- *Separate build (npm), zero risk to VSIX.*

### Phase 4 — Antigravity  [ ]
- Package Phase 3 as `.vsix` (`vsce package`), publish to OpenVSX (Antigravity's marketplace source) and document manual install (`Install from VSIX`).
- Smoke-test in Antigravity: DocumentColorProvider is core VS Code API, expected to work unmodified; record any divergence here.
- *Files: 0 code; packaging + docs only.*

### Phase 5 — Penguin Palette rollout  [ ]
- Apply palette to `UnrealClassifier.cs`: add Specifier/MetaKey/Delegate classifications, add light-theme variants (detect theme via `EnvironmentColors` or register light `ClassificationFormatDefinition` values).
- Mirror palette in the VS Code extension as an optional TextMate/semantic-token theme snippet in its README.
- Update CLAUDE.md directory map + shortcuts doc if any command added.

## Context-budget protocol (200K window)

- This file is the single source of truth — **re-read it instead of relying on chat history**.
- One phase per session; end each session by ticking the box and appending a 2-line "done/learned" note under the phase.
- Memory file `color-field-plan.md` holds only status + pointer here, never plan content.
- Never paste whole files into chat; reference `file:line`.
