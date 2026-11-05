# Penguin Packs — plan

Goal: make PenguinExtension work like a plugin host. Engine/library support (Unreal, SDL3, Unity, Godot, …)
lives in **packs** — folders with a `pack.json` — instead of being hard-coded. The core extension only knows how to
find packs, detect them, turn them on/off, and feed their data to the existing features.

## Decisions

| Topic | Decision |
|---|---|
| Pack type | **Data packs first** (JSON + library files). DLL plugins (`IPenguinPack`) come in phase 6. |
| Storage | Built-in (shipped in the VSIX) → user-global `%LOCALAPPDATA%\PenguinExtension\Packs` → per-solution `<solution>\.penguin\packs`. Same `id` later in that list wins. |
| On/off | Per solution, stored in `<solution>\.vs\PenguinExtension\packs.json`. No entry = "on if detected". |
| Languages | C/C++ now; C# and GDScript scanners in phases 4–5. |
| Editors | Visual Studio and VS Code, equal priority, reading the same packs. |
| Order | Unreal → SDL3 → Unity → Godot. |
| Getting packs | Built-in catalog of official packs + import a local folder/zip. No Git URLs. |
| Pricing | Everything is free (donations welcome); navigation/refactoring commands work in any pack. |

## Folder layout

```
<VSIX install dir>\Packs\           built-in packs (unreal, sdl3)
%LOCALAPPDATA%\PenguinExtension\Packs\   imported / downloaded packs (user-global)
<solution>\.penguin\packs\          packs committed with a project
<solution>\.vs\PenguinExtension\packs.json   per-solution check marks
```

A pack folder:

```
sdl3\
  pack.json          required
  snippets.json      optional
  specifiers.json    optional (macro → specifier lists, e.g. UPROPERTY)
  include\...        optional library headers (an imported library)
```

## `pack.json`

```jsonc
{
  "id": "sdl3",                       // required, lowercase letters/digits/dashes, unique
  "name": "SDL 3",                    // shown in the Packs window
  "version": "1.0.0",
  "description": "Simple DirectMedia Layer 3",
  "languages": ["cpp"],               // cpp | csharp | gdscript (gdscript: phase 5). Snippets/callbacks take the first one

  "detect": {                         // pack is "detected" when ANY rule matches
    "files": ["*.uproject"],          // looked up in the solution dir and up to 4 parents (wildcards ok, or a relative path)
    "includeHeaders": ["SDL3/SDL.h"]  // a project include directory containing this file belongs to this pack
  },

  "includeRoots": ["include"],        // folders inside the pack to index (imported libraries)
  "specifiers": "specifiers.json",
  "snippets": "snippets.json",
  "callbacks": "callbacks.json",      // engine-called methods (Unity Update, OnTriggerEnter…): completion stubs + hover docs
  "docsUrl": "https://docs.unity3d.com/{version}/Documentation/ScriptReference/{page}.html", // {version} = engine docs version
  "localDocs": "{editor}/Data/Documentation/en/ScriptReference/{page}.html",                // tried first when installed
  "mcp": [ { "name": "Unity (MCP for Unity)", "type": "http", "url": "http://localhost:8080/mcp", "hint": "…" } ]
}
```

`callbacks.json`:
```json
{ "language": "csharp", "defaultOwner": "MonoBehaviour",
  "callbacks": [ { "owner": "MonoBehaviour", "name": "OnTriggerEnter", "signature": "void OnTriggerEnter(Collider other)",
                   "description": "…", "docPage": "MonoBehaviour.OnTriggerEnter", "access": "private" } ] }
```
A class whose base type is an `owner` gets that owner's callbacks; any other base gets `defaultOwner`'s.
Snippet `text` may use `$0` (caret) and `$CLASS` (enclosing class name); extra lines get the current indentation.

`mcp` entries use the same keys as `.mcp.json` (`type` stdio/http/sse, `command`, `args`, `env`, `cwd`, `url`, `headers`).

Planned fields (later phases; unknown fields are ignored today):
`prefixes` (ranking/coloring, e.g. `SDL_`), `colors`,
`sources` (`cpp-headers`, `csharp`, `xmldoc`, `gdscript`, `godot-api`), `rules` (pack-defined regex extractors,
always run with a timeout so a bad pattern can't freeze VS).

`specifiers.json`:
```json
{ "macros": { "UPROPERTY": [ { "name": "Category", "description": "…", "insert": "Category = \"\"" } ] } }
```

`snippets.json`:
```json
{ "snippets": [ { "keyword": "SDL_Init", "text": "SDL_Init(SDL_INIT_VIDEO)", "description": "…", "caretBack": 1 } ] }
```

## Database

- `symbols.pack_id` and `indexed_files.pack_id` (schema v3). `NULL` = the solution's own code.
- Engine source is tagged `unreal`. Include roots claimed by a pack get that pack's id; unclaimed library roots get `external`.
- Unchecking a pack deletes its files' symbols on the next index pass. An unchanged file whose pack changed is re-tagged without re-parsing.

## Phases

### Phase 1 — pack core ✅ (this change)
- [x] `pack.json` model + loader for the three locations, validation errors logged and shown in Self-Test
- [x] Detection rules (`files`, `includeHeaders`), per-solution `packs.json` state
- [x] `pack_id` columns (schema v3), indexer tags files, purges unchecked packs
- [x] Unreal specifiers + snippets moved out of C# into the built-in `unreal` pack (same data, generated from the old tables)
- [x] Built-in `sdl3` pack (detection + snippets); claimed SDL3 include roots tagged `sdl3`
- [x] Self-Test lists packs

### Phase 2 — Packs window + import ✅ (catalog still open)
- [x] Packs window (Alt+U, Shift+P, `UI/PacksWindow.cs`): check mark per pack (this solution), Auto (follow detection), Folder, Remove, Reload.
- [x] Import pack folder / pack .zip (zip-slip checked) / library folder (wrapped in a new pack, `includeRoots` = its `include/` or root)
      into the user-global folder or the solution's `.penguin\packs` (`Services/PackImporter.cs`). After an import: "turn it on now?".
- [ ] Built-in catalog: `catalog.json` in this repo's `packs/` folder on GitHub; download → unzip into the user folder.

### Phase 3 — VS Code loader
- TypeScript port of the loader in `apps/vscode-ext`. Shared `pack.schema.json` + fixture packs under `docs/fixtures/packs` keep both loaders in sync.

### Phase 4 — Unity pack (C#) ✅ (first cut)
- [x] Unity mode in `UnrealProjectDetector`: `Assets/` + `ProjectSettings/ProjectVersion.txt` → version, matching editor
      (`Services/UnityEditorLocator.cs`: Unity Hub default folder, Hub `secondaryInstallPath.json`, `editors(-v2).json`,
      the generated .csproj HintPath, old installers; Options → Unity Editor Override), docs version ("2022.3", "6000.0", "560").
      No C++ indexing in Unity mode (Library/PackageCache is huge); C# IntelliSense stays Visual Studio's.
- [x] Built-in `unity` pack: 78 messages (MonoBehaviour, ScriptableObject, Editor, EditorWindow, StateMachineBehaviour),
      26 snippets (Inspector attributes, coroutines, singleton, raycast…), docs links for the project's version, MCP suggestion.
- [x] `CSharp` content type: completion source + commit manager (`Completion/CSharpPackCompletion.cs`; stubs with indentation
      and caret) and hover docs (`QuickInfo/CSharpPackQuickInfo.cs`). Class/base/depth from `Completion/CSharpPackContext.cs`.
- [ ] `UnityEngine.xml` doc source for API hover (Roslyn already shows these docs today).

### MCP client ✅
- `Services/McpClient.cs` (JSON-RPC: initialize, tools/list with paging, tools/call, ping; answers server ping/roots/list),
  transports stdio (newline JSON; `.cmd` shims via cmd.exe; tree kill), streamable HTTP (JSON or SSE replies, `Mcp-Session-Id`),
  legacy HTTP+SSE, and auto-fallback HTTP → SSE on 404/405.
- `Services/McpConfig.cs` reads `.mcp.json`, `.vs\mcp.json`, `.vscode\mcp.json`, `%USERPROFILE%\.mcp.json` (`servers` or `mcpServers`,
  `${workspaceFolder}`, `${env:X}`), plus enabled packs' `mcp` suggestions. `Services/McpHub.cs` keeps connections per solution.
- MCP window (Alt+U, Shift+M, `UI/McpWindow.cs`): connect (only on click), tools, JSON arguments pre-filled from the schema, call, result.

### Phase 5 — Godot pack
- `extension_api.json` + `doc/classes/*.xml` sources; godot-cpp (C++), Godot C#, then GDScript (new content type, classifier, completion) in VS and VS Code.

### Phase 6 — DLL plugins
- `IPenguinPack` interface loaded via MEF from pack folders.

## Rules that change
- CLAUDE.md hard rule 4: C/C++ features use `ContentType "C/C++"`; pack features for C# use `"CSharp"` (done in phase 4).
- Detection moves from `UnrealProjectDetector` into pack `detect` rules as more packs arrive.
