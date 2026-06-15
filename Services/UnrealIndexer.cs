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
            @"UPROPERTY\s*\(([^)]*)\)\s*([\w:*&<>\s]+?)\s+(\w+)\s*(?:[=;{])",
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
            var files = new List<(string path, bool isEngine)>();

            // Collect project source files
            if (!string.IsNullOrEmpty(projectSourceRoot) && Directory.Exists(projectSourceRoot))
            {
                foreach (var f in EnumerateSourceFiles(projectSourceRoot))
                    files.Add((f, false));
            }

            // Collect engine source files
            if (indexEngine && !string.IsNullOrEmpty(engineSourceRoot) && Directory.Exists(engineSourceRoot))
            {
                foreach (var f in EnumerateSourceFiles(engineSourceRoot))
                    files.Add((f, true));
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
                        var processed = await IndexSingleFileAsync(filePath, isEngine, ct).ConfigureAwait(false);
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

            var hash = ComputeHash(content);

            // Check if file has changed since last index
            var existingFile = await _db.GetIndexedFileAsync(filePath).ConfigureAwait(false);
            if (existingFile != null && existingFile.ContentHash == hash)
                return false; // unchanged

            // Extract symbols
            var symbols = ExtractSymbols(content, filePath, isEngine);
            var classInfos = ExtractClassInfos(content, filePath);

            if (symbols.Count > 0)
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

        // ── Symbol extraction ───────────────────────────────────────

        private List<UnrealSymbol> ExtractSymbols(string content, string filePath, bool isEngine)
        {
            var symbols = new List<UnrealSymbol>();
            var lines = content.Split('\n');
            var now = DateTime.UtcNow;



            // Extract UCLASS declarations
            foreach (Match m in RxUClass.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                var comment = ExtractPrecedingComment(content, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[2].Value,
                    FullyQualifiedName = m.Groups[2].Value,
                    Kind = UnrealSymbolKind.Class,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = m.Value.Trim(),
                    Comment = comment,
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            // Extract USTRUCT declarations
            foreach (Match m in RxUStruct.Matches(content))
            {
                var lineNum = GetLineNumber(content, m.Index);
                var comment = ExtractPrecedingComment(content, m.Index);
                symbols.Add(new UnrealSymbol
                {
                    Name = m.Groups[2].Value,
                    FullyQualifiedName = m.Groups[2].Value,
                    Kind = UnrealSymbolKind.Struct,
                    FilePath = filePath,
                    LineNumber = lineNum,
                    Signature = m.Value.Trim(),
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
                    Signature = m.Value.Trim(),
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
                    Signature = m.Value.Trim(),
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
                var comment = ExtractPrecedingComment(content, m.Index);

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
                var comment = ExtractPrecedingComment(content, m.Index);

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
                    Signature = m.Value.Trim(),
                    IsEngineSymbol = isEngine,
                    LastIndexed = now
                });
            }

            return symbols;
        }

        private List<UnrealClassInfo> ExtractClassInfos(string content, string filePath)
        {
            var infos = new List<UnrealClassInfo>();

            foreach (Match m in RxUClass.Matches(content))
            {
                infos.Add(new UnrealClassInfo
                {
                    ClassName = m.Groups[2].Value,
                    BaseClass = m.Groups[3].Success ? m.Groups[3].Value : null,
                    MetaSpecifiers = m.Groups[1].Value.Trim(),
                    IsAbstract = m.Groups[1].Value.Contains("Abstract")
                });
            }

            foreach (Match m in RxUStruct.Matches(content))
            {
                infos.Add(new UnrealClassInfo
                {
                    ClassName = m.Groups[2].Value,
                    BaseClass = m.Groups[3].Success ? m.Groups[3].Value : null,
                    MetaSpecifiers = m.Groups[1].Value.Trim()
                });
            }

            foreach (Match m in RxUInterface.Matches(content))
            {
                infos.Add(new UnrealClassInfo
                {
                    ClassName = m.Groups[2].Value,
                    BaseClass = m.Groups[3].Success ? m.Groups[3].Value : null,
                    MetaSpecifiers = m.Groups[1].Value.Trim()
                });
            }

            return infos;
        }

        // ── Helpers ─────────────────────────────────────────────────

        /// <summary>Enumerates .h and .cpp files recursively, skipping Intermediate/Binaries directories.</summary>
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
                    string.Equals(dirName, ".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (var f in files)
                {
                    var ext = Path.GetExtension(f);
                    if (string.Equals(ext, ".h", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".hpp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".cpp", StringComparison.OrdinalIgnoreCase))
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
