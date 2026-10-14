# SYNTAX_COLOR_PLAN.md — Penguin Color Theme for Visual Studio

**Goal (confirmed with the user 2026-10-14):** make C++/Unreal code in Visual Studio Community look great with a
Penguin color theme, plus a **Penguin Color Theme window** where the user picks the color of every kind of code
(keywords like `void`, `UPROPERTY`, functions, classes, enums, strings, comments, …) starting from a default theme.

Decisions: default = **Penguin Dark** (VS Dark+ base, tuned for Unreal) · scope = **everything in C++** (VS's own
categories + Penguin's Unreal categories) · UI = **dockable tool window with live preview** · **dark + light** sets.
Not in scope: FColor/FLinearColor swatches (an earlier misreading).

Work one phase per session; tick the box and add a one-line note under the phase when done.

## How it works

- **Theme entry** = one row in the window (e.g. "Class"). Each entry maps to one or more *classification types*:
  VS built-ins (`keyword`, `comment`, `string`, `number`, `operator`, `preprocessor keyword`), VS C++ semantic
  types (`cppRefType`, `cppFunction`, … — names verified in `Microsoft.VisualStudio.VC.dll`), and Penguin's own
  (`Penguin Unreal Type`, … from `Editor/UnrealClassifier.cs`). So "Class" colors classes the same whether VC++
  IntelliSense or the Penguin index recognized them.
- **ThemeService** writes colors at runtime through `IClassificationFormatMapService.GetClassificationFormatMap("text")`
  (one `BeginBatchUpdate/EndBatchUpdate`), on package load, on save in the window, and on `VSColorTheme.ThemeChanged`
  (dark vs light picked from the editor background luminance).
- **Storage:** `%LOCALAPPDATA%\PenguinExtension\theme.json` (per user, all solutions): preset name + per-entry
  overrides `{dark?, light?, bold?, italic?}`. Only overrides are stored, so new defaults reach users who didn't
  change that entry. Export/Import = the same JSON.
- **Off switch:** Options → "Use Penguin Color Theme" (default on). Off = restore the format map to VS's values
  captured before the first apply, so Fonts and Colors behaves exactly as stock VS.

## Default theme — Penguin Dark / Penguin Light

| Group | Entry | Classification types | Dark | Light |
|-------|-------|----------------------|------|-------|
| Language | Keyword (`void`, `class`, `const`) | `keyword` | `#569CD6` | `#0000FF` |
| | Control keyword (`if`, `for`, `return`) | `cppControlKeyword` | `#D8A0DF` | `#8F08C4` |
| | Preprocessor (`#include`) | `preprocessor keyword` | `#9B9B9B` | `#808080` |
| | String | `string`, `cppStringDelimiterCharacter` | `#CE9178` | `#A31515` |
| | Escape character (`\n`) | `cppStringEscapeCharacter` | `#FFD68F` | `#EE0000` |
| | Number | `number`, `cppUserDefinedLiteralNumber` | `#B5CEA8` | `#098658` |
| | Comment | `comment` | `#6A9955` *italic* | `#008000` *italic* |
| | Operator | `operator`, `cppOperator`, `cppMemberOperator` | `#D4D4D4` | `#000000` |
| | Namespace | `cppNamespace` | `#C8C8C8` | `#333333` |
| Types | Class | `cppRefType`, `cppType`, `Penguin Unreal Type` | `#4EC9B0` | `#267F99` |
| | Struct | `cppValueType`, `Penguin Unreal Struct` | `#86C691` | `#2E7D32` |
| | Enum | `Penguin Unreal Enum` | `#EE9D5B` | `#B35A00` |
| | Template | `cppClassTemplate`, `cppGenericType` | `#4EC9B0` | `#267F99` |
| | Delegate / event | `cppEvent`, `Penguin Unreal Delegate` | `#D16D9E` | `#A4286A` |
| Members | Function | `cppFunction`, `cppFunctionTemplate`, `Penguin Function` | `#DCDCAA` | `#795E26` |
| | Member function | `cppMemberFunction`, `cppStaticMemberFunction` | `#DCDCAA` | `#795E26` |
| | Property / field | `cppMemberField`, `cppStaticMemberField`, `cppProperty`, `Penguin Property` | `#9CDCFE` | `#001080` |
| | Local variable | `cppLocalVariable` | `#D4D4D4` | `#1F1F1F` |
| | Parameter | `cppParameter` | `#9A9A9A` | `#808080` |
| | Global variable | `cppGlobalVariable` | `#9CDCFE` | `#001080` |
| | Enum value | `cppEnumerator`, `Penguin Enum Value` | `#B8D7A3` | `#4F7A28` |
| | Label | `cppLabel` | `#C8C8C8` | `#000000` |
| Unreal | UE macro (`UPROPERTY`, `UE_LOG`) | `Penguin Unreal Macro` | `#C586C0` **bold** | `#AF00DB` **bold** |
| | Other macro | `cppMacro` | `#BEB7FF` | `#8A1BFF` |
| | Specifier (`EditAnywhere`) | `Penguin Macro Specifier` | `#569CD6` | `#0451A5` |
| | Meta key (`meta=(ClampMin)`) | `Penguin Meta Key` | `#D7BA7D` | `#8B6914` |

Penguin-only coloring rules (already built): project properties colored everywhere, engine properties only after
`->`/`.`; every key inside `UPROPERTY/UFUNCTION/UCLASS/USTRUCT/UENUM/UINTERFACE/UPARAM/UMETA/UDELEGATE(…)` is a
specifier, keys inside `meta=(…)` are meta keys, multi-line macro arguments supported.

## Phases

### Phase 0 — Unreal categories in the classifier  [x]
- Done (uncommitted at time of writing): `Editor/UnrealClassifier.cs` struct/enum/delegate/property/specifier/meta
  categories, `Editor/PenguinPalette.cs`, `CacheService.NameCategory`, self-test "Specifier coloring" check.

### Phase 1 — Spike: prove the mechanism  [~] built, needs the in-VS check below
- `Services/ThemeService.cs` (minimal): apply hard-coded Penguin Dark through the format map.
- Self-test lists every classification name from the table as found/missing (some `cpp*` names may not be
  registered until a C++ file opens — record which).
- Verify in VS: (a) runtime colors win over Fonts and Colors values, (b) they are **not** written into the user's
  Fonts and Colors settings, (c) where VC++ and Penguin both classify a span, the theme color still shows (fix with
  format-map priority if not). Record the findings here.
- Built 2026-10-14 together with Phases 2–4 (user asked to complete the phases in one go). ThemeModel logic
  unit-checked outside VS (14 checks); (a)–(c) still need the user's run in VS.

### Phase 2 — Theme model + default themes  [x]
- `Services/ThemeModel.cs`: entries, groups, the table above as the built-in Penguin Dark/Light (replaces
  `PenguinPalette.cs`), JSON load/save, export/import.
- `ThemeService`: load at package start (background read, main-thread apply), dark/light switching on
  `VSColorTheme.ThemeChanged`, capture-and-restore VS values for the off switch, options toggle.
- Result: VS looks like the Penguin theme with no UI yet.
- Done: `Services/ThemeModel.cs` (`ThemeCatalog` replaced `PenguinPalette.cs`; the Penguin classification formats
  take their Fonts and Colors defaults from it), `Services/ThemeService.cs`. The on/off switch lives in theme.json
  (`Enabled`) and the window, not the Options page.

### Phase 3 — Penguin Color Theme window  [x]
- `UI/ColorThemeWindow.cs` (+ control, code-built WPF in the style of the other Penguin windows): preset dropdown
  (Penguin Dark/Light, Custom), entries grouped Language/Types/Members/Unreal; per row: swatch button (opens
  `System.Windows.Forms.ColorDialog` with the theme's colors as custom colors), hex box, Bold/Italic toggles,
  per-row reset. Live preview pane: a fixed Unreal sample (class, UPROPERTY with meta, function, enum, strings,
  comment) drawn from WPF runs bound to the entries, so every entry is visible. Buttons: Reset all, Export,
  Import. Edits apply to open editors immediately and save to theme.json.
- Done: `UI/ColorThemeWindow.cs`. The preset dropdown became a Dark colors / Light colors selector (each entry
  stores both).
- Command + menu item + shortcut **Alt+U, Shift+T** (all 26 letters after Alt+U are taken). Add to the .vsct,
  `PenguinExtensionCommandIds`, Shortcuts Reference window, CLAUDE.md shortcut table.

### Phase 4 — Polish  [x]
- Self-test: theme on/off, preset, dark/light, missing classification names. PenguinLog for every apply.
- README/CLAUDE.md: theme window, how it relates to Fonts and Colors, the off switch.

### Phase 5 — VS Code / Antigravity (later)  [ ]
- Rust `ue-lsp` semantic tokens for the Unreal categories; `apps/vscode-ext` contributes the Penguin theme as
  `editor.semanticTokenColorCustomizations` defaults; "Export for VS Code" button in the window writes the
  user's current colors in that format. Antigravity uses the same extension.

## Context-budget protocol (200K window)
- This file is the source of truth; re-read it rather than relying on chat history.
- Memory `color-field-plan.md` holds only status + pointer here.
