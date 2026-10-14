using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PenguinExtention.Database;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Progress data reported by <see cref="UnrealIndexer"/> during scanning.
    /// </summary>
    internal sealed class IndexProgress
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int SymbolsFound { get; set; }
        public string CurrentFile { get; set; }
        public bool IsComplete { get; set; }
    }

    /// <summary>
    /// Heavyweight regex-based scanner that extracts Unreal Engine symbols from C++ source files.
    /// Handles UCLASS, USTRUCT, UENUM, UFUNCTION, UPROPERTY, UDELEGATE, and UINTERFACE macros
    /// across UE 4.27, 5.x, and future 6.x header layouts.
    /// </summary>
    internal sealed class UnrealIndexer
    {
        private readonly SQLiteCache _db;
        private readonly CacheService _cache;
        private readonly int _maxParallelism;

        // ── Regex patterns ──────────────────────────────────────────
        // Compiled for performance (these are called thousands of times).

        // UCLASS(meta...) class [MODULE_API] ClassName [: public BaseClass]
        private static readonly Regex RxUClass = new Regex(
            @"UCLASS\s*\(([^)]*)\)\s*class\s+(?:\w+_API\s+)?(\w+)\s*(?::\s*public\s+(\w+))?",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // USTRUCT(meta...) struct [MODULE_API] StructName
        private static readonly Regex RxUStruct = new Regex(
            @"USTRUCT\s*\(([^)]*)\)\s*struct\s+(?:\w+_API\s+)?(\w+)\s*(?::\s*public\s+(\w+))?",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // UENUM(meta...) enum [class] EnumName
        private static readonly Regex RxUEnum = new Regex(
            @"UENUM\s*\(([^)]*)\)\s*enum\s+(?:class\s+)?(\w+)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // UFUNCTION(meta...) [virtual] ReturnType FuncName(params) [const] [override]
        private static readonly Regex RxUFunction = new Regex(
            @"UFUNCTION\s*\(([^)]*)\)\s*(?:virtual\s+)?(?:static\s+)?([\w:*&<>\s]+?)\s+(\w+)\s*\(([^)]*)\)",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // UPROPERTY(meta...) Type PropertyName [= default] ;
        private static readonly Regex RxUProperty = new Regex(
            @"UPROPERTY\s*\(([^)]*)\)\s*([\w:*&<>,\s]+?)[\s\*&]+([A-Za-z_]\w*)\s*(?:\[[^\]]*\]\s*)?(?::\s*\d+\s*)?(?:[=;{])", // bit-fields: "uint8 bX : 1;"
            RegexOptions.Compiled | RegexOptions.Singleline);

        // UINTERFACE(meta...) class UInterfaceName : public UInterface
        private static readonly Regex RxUInterface = new Regex(
            @"UINTERFACE\s*\(([^)]*)\)\s*class\s+(?:\w+_API\s+)?(\w+)\s*(?::\s*public\s+(\w+))?",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // DECLARE_DYNAMIC_MULTICAST_DELEGATE_* / DECLARE_DELEGATE_* macros
        private static readonly Regex RxDelegate = new Regex(
            @"DECLARE_(?:DYNAMIC_(?:MULTICAST_)?)?DELEGATE\w*\s*\(\s*(\w+)",
            RegexOptions.Compiled);

        // Simple class/struct detection (non-UCLASS) for navigation support
        private static readonly Regex RxPlainClass = new Regex(
            @"^(?:class|struct)\s+(?:\w+_API\s+)?(\w+)\s*(?:final\s*)?(?::\s*public\s+(\w+))?",
            RegexOptions.Compiled | RegexOptions.Multiline);

        // Doc-comment extractor (/** ... */ or /// preceding a line)
        private static readonly Regex RxDocComment = new Regex(
            @"/\*\*\s*(.*?)\*/|///\s*(.*?)$",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.Multiline);

        // ── Constructor ─────────────────────────────────────────────

        public UnrealIndexer(SQLiteCache db, CacheService cache, int maxParallelism)
        {
            _db = db;
            _cache = cache;
            _maxParallelism = Math.Max(1, maxParallelism);
        }

        // ── Full index ──────────────────────────────────────────────

        /// <summary>
        /// Performs a full scan of project and (optionally) engine source.
        /// Skips files whose content hash matches the last indexed version.
        /// </summary>
        public async Task IndexAsync(
            string projectSourceRoot,
            string engineSourceRoot,
            bool indexEngine,
            IProgress<IndexProgress> progress,
            CancellationToken ct)
        {
            // Purge entries for files that no longer exist (moved/renamed project, deleted sources) so
            // navigation never points at missing files.
            try
            {
                int purged = 0;
                foreach (var stale in (await _db.GetAllFilePathsAsync().ConfigureAwait(false)).Where(p => !File.Exists(p)))
                {
                    ct.ThrowIfCancellationRequested();
                    await _cache.RemoveFileSymbolsAsync(stale).ConfigureAwait(false);
                    purged++;
                }
                if (purged > 0) PenguinLog.Info("Index", "purged " + purged + " missing file(s) from the index.");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { PenguinLog.Error("Index", "purge failed: " + ex.Message); }

            var files = new List<(string path, bool isEngine)>();

            // Collect project source files (Source/ plus project Plugins/)
            foreach (var root in ProjectRoots(projectSourceRoot))
                foreach (var f in EnumerateSourceFiles(root))
                    files.Add((f, false));

            // Collect engine source files
            if (indexEngine && !string.IsNullOrEmpty(engineSourceRoot) && Directory.Exists(engineSourceRoot))
            {
                foreach (var f in EnumerateSourceFiles(engineSourceRoot))
                    files.Add((f, true));
            }

            // External library include roots (e.g. SDL2/include) — headers only, treated like engine symbols
            var seen = new HashSet<string>(files.Select(f => f.path), StringComparer.OrdinalIgnoreCase);
            foreach (var root in ExternalRoots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var f in EnumerateSourceFiles(root))
                    if (!HeaderSourceResolver.IsSource(f) && seen.Add(f)) files.Add((f, true));
            }

            var progressData = new IndexProgress { TotalFiles = files.Count };

            // Process files in parallel with throttled parallelism
            var semaphore = new SemaphoreSlim(_maxParallelism);
            var tasks = new List<Task>();

            foreach (var (filePath, isEngine) in files)
            {
                ct.ThrowIfCancellationRequested();

                await semaphore.WaitAsync(ct).ConfigureAwait(false);

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        bool processed = false;
                        try { processed = await IndexSingleFileAsync(filePath, isEngine, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { PenguinLog.Error("Index", filePath + ": " + ex.Message); } // one bad file never aborts the pass
                        lock (progressData)
                        {
                            if (processed)
                                progressData.ProcessedFiles++;
                            else
                                progressData.SkippedFiles++;
                            progressData.CurrentFile = Path.GetFileName(filePath);
                        }
                        progress?.Report(progressData);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progressData.IsComplete = true;
            progressData.SymbolsFound = _cache.SymbolCount;
            progress?.Report(progressData);
        }

        // ── Single-file indexing ────────────────────────────────────

        /// <summary>
        /// Index a single file.  Returns true if the file was actually re-indexed (hash changed).
        /// </summary>
        public async Task<bool> IndexSingleFileAsync(string filePath, bool isEngine, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string content;
            try
            {
                content = File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch
            {
                return false; // unreadable file
            }

            var hash = ComputeHash(ExtractorVersion + content);

            // Check if file has changed since last index
            var existingFile = await _db.GetIndexedFileAsync(filePath).ConfigureAwait(false);
            if (existingFile != null && existingFile.ContentHash == hash)
                return false; // unchanged

            // Extract symbols: Unreal reflection macros, then general C++ declarations
            var symbols = ExtractSymbols(content, filePath, isEngine);
            var classInfos = ExtractClassInfos(content, filePath);
            MergeClassMembers(content, filePath, isEngine, symbols);
            if (!isEngine || IndexExternalAsCpp)
                MergeGeneralCpp(content, filePath, isEngine, symbols, classInfos);

            // Also runs for an empty result when the file was indexed before, so removed declarations disappear.
            if (symbols.Count > 0 || existingFile != null)
            {
                await _cache.UpdateSymbolsAsync(filePath, symbols, classInfos).ConfigureAwait(false);
            }

            // Update indexed_files tracking
            await _db.UpsertIndexedFileAsync(new IndexedFile
            {
                FilePath = filePath,
                ContentHash = hash,
                LastIndexed = DateTime.UtcNow,
                IsEngineFile = isEngine
            }).ConfigureAwait(false);

            return true;
        }

        /// <summary>
        /// True for plain C/C++ solutions (e.g. SDL2 games): external include roots are parsed with the general
        /// C++ extractor. False for Unreal, where engine sources are far too large for it.
        /// </summary>
        public bool IndexExternalAsCpp { get; set; }

        /// <summary>Additional library include directories (headers indexed as external symbols).</summary>
        public IReadOnlyList<string> ExternalRoots { get; set; }

        /// <summary>Adds general C++ declarations not already covered by a reflection-macro symbol (same name within 3 lines).</summary>
        private static void MergeGeneralCpp(string content, string filePath, bool isExternal, List<UnrealSymbol> symbols, List<UnrealClassInfo> classInfos)
        {
            var general = new List<UnrealSymbol>();
            var generalInfos = new List<UnrealClassInfo>();
            try { CppSymbolExtractor.Extract(content, filePath, isExternal, general, generalInfos); }
            catch (Exception ex) { PenguinLog.Error("Index", "C++ extractor: " + filePath + ": " + ex.Message); return; }

            var existing = symbols.ToLookup(s => s.Name, StringComparer.Ordinal);
            foreach (var g in general)
                if (!existing[g.Name].Any(s => Math.Abs(s.LineNumber - g.LineNumber) <= 3))
                    symbols.Add(g);
            var knownInfos = new HashSet<string>(classInfos.Select(c => c.ClassName), StringComparer.Ordinal);
            foreach (var ci in generalInfos)
                if (knownInfos.Add(ci.ClassName)) classInfos.Add(ci);
        }

        // ── Occurrence pass (Find References / Rename) ──────────────

        private int _fingerprintVersion = -1;
        private string _fingerprint;
        private HashSet<string> _fingerprintNames;

        /// <summary>
        /// Rescans project files for occurrences of known symbol names. Runs after symbol indexing, because
        /// a file can reference names declared in files indexed later. Unchanged files are skipped.
        /// </summary>
        public async Task ScanOccurrencesAsync(string projectSourceRoot, CancellationToken ct)
        {
            var semaphore = new SemaphoreSlim(_maxParallelism);
            var tasks = new List<Task>();
            foreach (var root in ProjectRoots(projectSourceRoot))
                foreach (var filePath in EnumerateSourceFiles(root))
                {
                    ct.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    tasks.Add(Task.Run(async () =>
                    {
                        try { await ScanFileOccurrencesAsync(filePath, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { PenguinLog.Error("References", filePath + ": " + ex.Message); }
                        finally { semaphore.Release(); }
                    }, ct));
                }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>The project's Source folder plus its sibling Plugins folder (project plugins are project code).</summary>
        internal static IEnumerable<string> ProjectRoots(string projectSourceRoot)
        {
            if (string.IsNullOrEmpty(projectSourceRoot) || !Directory.Exists(projectSourceRoot)) yield break;
            yield return projectSourceRoot;
            var parent = Path.GetDirectoryName(projectSourceRoot.TrimEnd('\\', '/'));
            var plugins = parent == null ? null : Path.Combine(parent, "Plugins");
            if (plugins != null && Directory.Exists(plugins)) yield return plugins;
        }

        /// <summary>Rescans one file's occurrences if its content or the known-name set changed.</summary>
        public async Task ScanFileOccurrencesAsync(string filePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string content;
            try { content = File.ReadAllText(filePath, Encoding.UTF8); }
            catch { return; }

            var names = KnownNames(out var fingerprint);
            if (names.Count == 0) return;

            var key = ComputeHash(content) + ":" + fingerprint;
            var stored = await _db.GetOccurrenceScanKeyAsync(filePath).ConfigureAwait(false);
            if (stored == key) return;

            var occurrences = OccurrenceScanner.Scan(content, names);
            foreach (var o in occurrences) o.FilePath = filePath;
            await _db.ReplaceOccurrencesForFileAsync(filePath, occurrences, key).ConfigureAwait(false);
        }

        /// <summary>Known names plus an order-independent, session-stable fingerprint of the set.</summary>
        private HashSet<string> KnownNames(out string fingerprint)
        {
            var names = _cache.GetKnownNames(out var version);
            lock (this)
            {
                if (version != _fingerprintVersion || !ReferenceEquals(names, _fingerprintNames))
                {
                    ulong sum = 0, xor = 0;
                    foreach (var name in names)
                    {
                        // FNV-1a: stable across processes, unlike string.GetHashCode.
                        ulong h = 14695981039346656037UL;
                        foreach (var ch in name) { h ^= ch; h *= 1099511628211UL; }
                        sum += h; xor ^= h;
                    }
                    _fingerprint = names.Count.ToString("x") + "-" + sum.ToString("x16") + xor.ToString("x16");
                    _fingerprintVersion = version;
                    _fingerprintNames = names;
                }
                fingerprint = _fingerprint;
            }
            return names;
        }

        // ── Symbol extraction ───────────────────────────────────────

        private static readonly Regex RxReflectionMacro = new Regex(
            @"\b(?:UCLASS|USTRUCT|UENUM|UFUNCTION|UPROPERTY|UINTERFACE|UPARAM|UMETA)\s*\(", RegexOptions.Compiled);

        /// <summary>
        /// Copy of <paramref name="content"/> where parentheses nested inside reflection-macro arguments become
        /// '[' / ']' (same length, so indices and line numbers are unchanged). Lets the macro regexes, whose
        /// argument pattern is [^)]*, handle UCLASS(meta=(ShortTooltip="…")) and UFUNCTION(meta=(DisplayName="…")).
        /// </summary>
        internal static string FlattenMacroArguments(string content)
        {
            char[] chars = null;
            foreach (Match m in RxReflectionMacro.Matches(content))
            {
                int depth = 0;
                bool inString = false;
                for (int i = m.Index + m.Length - 1; i < content.Length && i - m.Index < 4000; i++)
                {
                    char c = content[i];
                    if (c == '"' && content[i - 1] != '\\') { inString = !inString; continue; }
                    if (inString)
                    {
                        if (c == '(' || c == ')') { if (chars == null) chars = content.ToCharArray(); chars[i] = c == '(' ? '[' : ']'; }
                        continue;
                    }
                    if (c == '(')
                    {
                        if (depth++ > 0) { if (chars == null) chars = content.ToCharArray(); chars[i] = '['; }
                    }
                    else if (c == ')')
                    {
                        if (--depth == 0) break;
                        if (chars == null) chars = content.ToCharArray();
                        chars[i] = ']';
                    }
                }
            }
            return chars == null ? content : new string(chars);
        }

        private List<UnrealSymbol> ExtractSymbols(string original, string filePath, bool isEngine)
        {
            var symbols = new List<UnrealSymbol>();
            var now = DateTime.UtcNow;
            var content = FlattenMacroArguments(original);
            string Text(Match m) => original.Substring(m.Index, m.Length).Trim();

            // Extract UCLASS declarations
            foreach (Match m in RxUClass.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                var comment = ExtractPrecedingComment(original, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[2].Value,
                    FullyQualifiedName = m.Groups[2].Value,
                    Kind = UnrealSymbolKind.Class,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = Text(m),
                    Comment = comment,
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract USTRUCT declarations
            foreach (Match m in RxUStruct.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                var comment = ExtractPrecedingComment(original, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[2].Value,
                    FullyQualifiedName = m.Groups[2].Value,
                    Kind = UnrealSymbolKind.Struct,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = Text(m),
                    Comment = comment,
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract UENUM declarations
            foreach (Match m in RxUEnum.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[2].Value,
                    FullyQualifiedName = m.Groups[2].Value,
                    Kind = UnrealSymbolKind.Enum,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = Text(m),
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract UINTERFACE declarations
            foreach (Match m in RxUInterface.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[2].Value,
                    FullyQualifiedName = m.Groups[2].Value,
                    Kind = UnrealSymbolKind.Interface,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = Text(m),
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract UFUNCTION declarations — attribute to enclosing class
            foreach (Match m in RxUFunction.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                var ownerClass = FindEnclosingClass(content, m.Index);
                var returnType = m.Groups[2].Value.Trim();
                var funcName = m.Groups[3].Value;
                var parameters = m.Groups[4].Value.Trim();
                var comment = ExtractPrecedingComment(original, m.Index);

                symbols.Add(new UnrealSymbol
                {
                    Name = funcName,
                    FullyQualifiedName = ownerClass != null ? $"{ownerClass}::{funcName}" : funcName,
                    Kind = UnrealSymbolKind.Function,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    OwnerClass = ownerClass,
                    Signature = $"{returnType} {funcName}({parameters})",
                    ReturnType = returnType,
                    Comment = comment,
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract UPROPERTY declarations
            foreach (Match m in RxUProperty.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                var ownerClass = FindEnclosingClass(content, m.Index);
                var propType = m.Groups[2].Value.Trim();
                var propName = m.Groups[3].Value;
                var comment = ExtractPrecedingComment(original, m.Index);

                symbols.Add(new UnrealSymbol
                {
                    Name = propName,
                    FullyQualifiedName = ownerClass != null ? $"{ownerClass}::{propName}" : propName,
                    Kind = UnrealSymbolKind.Property,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    OwnerClass = ownerClass,
                    Signature = $"{propType} {propName}",
                    ReturnType = propType,
                    Comment = comment,
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract delegate declarations
            foreach (Match m in RxDelegate.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[1].Value,
                    FullyQualifiedName = m.Groups[1].Value,
                    Kind = UnrealSymbolKind.Delegate,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = Text(m),
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            return symbols;
        }

        private static readonly Regex RxAccessPrefix = new Regex(@"^\s*(?:(?:public|protected|private)\s*:(?!:)\s*)+", RegexOptions.Compiled);
        private static readonly Regex RxBodyMacroStatement = new Regex(
            @"^\s*(GENERATED_BODY|GENERATED_UCLASS_BODY|GENERATED_USTRUCT_BODY|GENERATED_IINTERFACE_BODY|UE_DEPRECATED|PRAGMA_\w+|UE_\w+_DEPRECATION\w*)\s*\([^)]*\)\s*",
            RegexOptions.Compiled);
        // Type Name [= init | {init} | [N]] — a plain data member (non-UPROPERTY fields still matter for completion)
        private static readonly Regex RxMemberField = new Regex(
            @"^(?<type>(?:(?:static|mutable|const|constexpr|inline)\s+)*[A-Za-z_][\w:]*(?:\s*<[^;{}()]*>)?(?:\s*[\*&]+|\s)\s*)(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]]*\]\s*)?(?::\s*\d+\s*)?(?:=.*|\{.*\})?$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>
        /// Every function and data member declared directly in each UCLASS/USTRUCT/UINTERFACE body — including plain
        /// C++ members (virtual BeginPlay, FORCEINLINE getters, non-UPROPERTY fields) that the reflection-macro regexes
        /// never see. Uses <see cref="CodeContext.Parse"/> on depth-0 statements of the (comment/string-masked) body.
        /// Members already produced from a UFUNCTION/UPROPERTY on the same line are skipped by the caller's merge.
        /// </summary>
        internal static List<UnrealSymbol> ExtractClassMembers(string original, string filePath, bool isEngine)
        {
            var members = new List<UnrealSymbol>();
            var masked = CodeContext.Mask(original);
            var flattened = FlattenMacroArguments(masked);
            var now = DateTime.UtcNow;
            var lineStarts = new List<int> { 0 };
            for (int p = 0; p < original.Length; p++) if (original[p] == '\n') lineStarts.Add(p + 1);
            int LineOf(int offset) { int ix = lineStarts.BinarySearch(offset); return (ix >= 0 ? ix : ~ix - 1) + 1; }
            var heads = RxUClass.Matches(flattened).Cast<Match>()
                .Concat(RxUStruct.Matches(flattened).Cast<Match>())
                .Concat(RxUInterface.Matches(flattened).Cast<Match>());
            foreach (var head in heads)
            {
                var owner = head.Groups[2].Value;
                int open = masked.IndexOf('{', head.Index + head.Length);
                if (open < 0) continue;
                int semicolon = masked.IndexOf(';', head.Index + head.Length);
                if (semicolon >= 0 && semicolon < open) continue; // forward declaration
                int close = CodeContext.MatchBrace(masked, open);
                if (close < 0) close = masked.Length;

                int start = open + 1, parens = 0;
                for (int i = open + 1; i < close; i++)
                {
                    char c = masked[i];
                    if (c == '(') parens++;
                    else if (c == ')') parens = Math.Max(0, parens - 1);
                    else if (parens > 0) continue;
                    else if (c == ';')
                    {
                        AddMember(masked.Substring(start, i - start), start, false);
                        start = i + 1;
                    }
                    else if (c == '{')
                    {
                        var statement = masked.Substring(start, i - start);
                        bool initializer = statement.TrimEnd().EndsWith("=") || RxMemberField.IsMatch(statement.Trim() + "{}") && !statement.Contains("(");
                        int end = CodeContext.MatchBrace(masked, i);
                        if (end < 0 || end > close) break;
                        if (!initializer && !Regex.IsMatch(statement, @"\b(class|struct|enum|union|namespace)\b"))
                            AddMember(statement, start, true);
                        // "Type Name{init};" / "Type Name = {…};" continue to their ';'
                        i = end;
                        if (initializer) continue;
                        int k = end + 1;
                        while (k < close && char.IsWhiteSpace(masked[k])) k++;
                        if (k < close && masked[k] == ';') i = k; // "struct X {…};" / "enum {…};"
                        start = i + 1;
                    }
                }

                void AddMember(string statement, int offset, bool hasBody)
                {
                    var stripped = RxAccessPrefix.Replace(statement, "");
                    while (true)
                    {
                        var macro = RxBodyMacroStatement.Match(stripped);
                        if (!macro.Success || macro.Length == 0) break;
                        stripped = stripped.Substring(macro.Length);
                    }
                    var text = stripped.Trim();
                    if (text.Length == 0 || text.StartsWith("friend") || text.StartsWith("using") || text.StartsWith("typedef") ||
                        text.StartsWith("static_assert") || text.StartsWith("UPROPERTY") || text.StartsWith("DECLARE_") || text.StartsWith("template")) return;
                    int lead = statement.Length - statement.TrimStart().Length + (statement.TrimStart().Length - stripped.TrimStart().Length);
                    int position = offset + Math.Max(0, lead);
                    var line = LineOf(position);

                    var decl = CodeContext.Parse(text);
                    if (decl != null)
                    {
                        if (decl.Name == owner || decl.Name.StartsWith("~") || decl.Name.StartsWith("operator")) return;
                        bool isVirtual = Regex.IsMatch(text, @"\bvirtual\b") || Regex.IsMatch(text, @"\boverride\b");
                        var signature = (isVirtual ? "virtual " : "") + (decl.ReturnType.Length > 0 ? decl.ReturnType + " " : "") + decl.Name +
                            "(" + string.Join(", ", decl.Parameters) + ")" + (decl.DefinitionQualifiers.Length > 0 ? " " + decl.DefinitionQualifiers : "") +
                            (decl.IsPureVirtual ? " = 0" : "");
                        members.Add(new UnrealSymbol
                        {
                            Name = decl.Name, FullyQualifiedName = owner + "::" + decl.Name, Kind = UnrealSymbolKind.Function,
                            FilePath = filePath, LineNumber = line, OwnerClass = owner, Signature = signature, ReturnType = decl.ReturnType,
                            Comment = ExtractPrecedingComment(original, position), IsEngineSymbol = isEngine, LastIndexed = now
                        });
                        return;
                    }
                    if (hasBody || text.Contains("(")) return;
                    var field = RxMemberField.Match(text);
                    if (!field.Success) return;
                    var type = Regex.Replace(field.Groups["type"].Value, @"\s+", " ").Trim();
                    var name = field.Groups["name"].Value;
                    if (type.Length == 0 || type == "return" || type == "delete" || type == "goto") return;
                    members.Add(new UnrealSymbol
                    {
                        Name = name, FullyQualifiedName = owner + "::" + name, Kind = UnrealSymbolKind.Variable,
                        FilePath = filePath, LineNumber = line, OwnerClass = owner, Signature = type + " " + name, ReturnType = type,
                        Comment = ExtractPrecedingComment(original, position), IsEngineSymbol = isEngine, LastIndexed = now
                    });
                }
            }
            return members;
        }

        /// <summary>Adds class members not already present from a reflection macro (same owner+name within 3 lines).</summary>
        private static void MergeClassMembers(string content, string filePath, bool isEngine, List<UnrealSymbol> symbols)
        {
            List<UnrealSymbol> members;
            try { members = ExtractClassMembers(content, filePath, isEngine); }
            catch (Exception ex) { PenguinLog.Error("Index", "class members: " + filePath + ": " + ex.Message); return; }
            var existing = symbols.Where(s => s.OwnerClass != null).ToLookup(s => s.OwnerClass + "::" + s.Name, StringComparer.Ordinal);
            foreach (var m in members)
                if (!existing[m.OwnerClass + "::" + m.Name].Any(s => Math.Abs(s.LineNumber - m.LineNumber) <= 3))
                    symbols.Add(m);
        }

        private List<UnrealClassInfo> ExtractClassInfos(string original, string filePath)
        {
            var content = FlattenMacroArguments(original);
            var infos = new List<UnrealClassInfo>();

            foreach (Match m in RxUClass.Matches(content))
            {
                infos.Add(new UnrealClassInfo
                {
                    ClassName = m.Groups[2].Value,
                    BaseClass = m.Groups[3].Success ? m.Groups[3].Value : null,
                    MetaSpecifiers = original.Substring(m.Groups[1].Index, m.Groups[1].Length).Trim(),
                    IsAbstract = m.Groups[1].Value.Contains("Abstract")
                });
            }

            foreach (Match m in RxUStruct.Matches(content))
            {
                infos.Add(new UnrealClassInfo
                {
                    ClassName = m.Groups[2].Value,
                    BaseClass = m.Groups[3].Success ? m.Groups[3].Value : null,
                    MetaSpecifiers = original.Substring(m.Groups[1].Index, m.Groups[1].Length).Trim()
                });
            }

            foreach (Match m in RxUInterface.Matches(content))
            {
                infos.Add(new UnrealClassInfo
                {
                    ClassName = m.Groups[2].Value,
                    BaseClass = m.Groups[3].Success ? m.Groups[3].Value : null,
                    MetaSpecifiers = original.Substring(m.Groups[1].Index, m.Groups[1].Length).Trim()
                });
            }

            return infos;
        }

        // ── Helpers ─────────────────────────────────────────────────

        /// <summary>Enumerates .h and .cpp files recursively, skipping Intermediate/Binaries directories.</summary>
        internal static readonly HashSet<string> SourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".h", ".hpp", ".hh", ".hxx", ".inl", ".cpp", ".cc", ".cxx", ".c" };

        // Build output / tooling folders of plain Visual Studio and CMake solutions.
        private static readonly HashSet<string> BuildOutputDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".vs", "x64", "x86", "Win32", "ARM64", "Debug", "Release", "RelWithDebInfo", "MinSizeRel", "obj", "bin", "out", "build", "cmake-build-debug", "cmake-build-release", "node_modules", "packages", "vcpkg_installed" };

        private static IEnumerable<string> EnumerateSourceFiles(string rootDir)
        {
            var stack = new Stack<string>();
            stack.Push(rootDir);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                var dirName = Path.GetFileName(dir);

                // Skip non-source directories
                if (string.Equals(dirName, "Intermediate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "Binaries", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "Saved", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "DerivedDataCache", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, "ThirdParty", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase) ||
                    BuildOutputDirs.Contains(dirName))
                    continue;

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (var f in files)
                {
                    if (SourceExtensions.Contains(Path.GetExtension(f)))
                    {
                        // Skip generated files (they don't contain meaningful declarations)
                        var fileName = Path.GetFileName(f);
                        if (fileName.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase))
                            continue;

                        yield return f;
                    }
                }

                try
                {
                    foreach (var subDir in Directory.GetDirectories(dir))
                        stack.Push(subDir);
                }
                catch { /* permission denied, etc. */ }
            }
        }

        /// <summary>Convert a character offset in the file content to a 1-based line number.</summary>
        private static int GetLineNumber(string content, int charOffset)
        {
            int line = 1;
            for (int i = 0; i < charOffset && i < content.Length; i++)
            {
                if (content[i] == '\n') line++;
            }
            return line;
        }

        /// <summary>Find the enclosing UCLASS/USTRUCT name for a member declaration.</summary>
        private string FindEnclosingClass(string content, int memberOffset)
        {
            // Walk backward looking for the nearest UCLASS/USTRUCT declaration
            var classMatches = RxUClass.Matches(content).Cast<Match>()
                .Concat(RxUStruct.Matches(content).Cast<Match>())
                .Concat(RxUInterface.Matches(content).Cast<Match>())
                .Where(m => m.Index < memberOffset)
                .OrderByDescending(m => m.Index)
                .FirstOrDefault();

            return classMatches?.Groups[2].Value;
        }

        /// <summary>Extract the doc-comment immediately preceding a declaration.</summary>
        private static string ExtractPrecedingComment(string content, int declOffset)
        {
            // Look backward up to 500 chars for a doc comment
            int searchStart = Math.Max(0, declOffset - 500);
            string preceding = content.Substring(searchStart, declOffset - searchStart);

            // Try /** ... */ style
            int blockEnd = preceding.LastIndexOf("*/", StringComparison.Ordinal);
            if (blockEnd >= 0)
            {
                int blockStart = preceding.LastIndexOf("/**", blockEnd, StringComparison.Ordinal);
                if (blockStart >= 0)
                {
                    var comment = preceding.Substring(blockStart + 3, blockEnd - blockStart - 3).Trim();
                    // Clean up asterisks
                    comment = Regex.Replace(comment, @"^\s*\*\s?", "", RegexOptions.Multiline).Trim();
                    if (comment.Length > 0) return comment;
                }
            }

            // Try /// style (take last contiguous block)
            var tripleSlash = new List<string>();
            var precedingLines = preceding.Split('\n');
            for (int i = precedingLines.Length - 1; i >= 0; i--)
            {
                var trimmed = precedingLines[i].Trim();
                if (trimmed.StartsWith("///"))
                {
                    tripleSlash.Insert(0, trimmed.Substring(3).Trim());
                }
                else if (tripleSlash.Count > 0)
                {
                    break;
                }
            }

            return tripleSlash.Count > 0 ? string.Join(" ", tripleSlash) : null;
        }

        /// <summary>Compute SHA-256 hash of file content for change detection.</summary>
        // Part of every file hash: bump when extraction changes so unchanged files are re-indexed once.
        private const string ExtractorVersion = "x3|";

        private static string ComputeHash(string content)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
                var sb = new StringBuilder(64);
                foreach (var b in bytes)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
