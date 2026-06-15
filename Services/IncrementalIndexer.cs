using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PenguinExtention.Database;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Watches the project source directory for file changes and triggers re-indexing
    /// of modified files.  Uses a 500ms debounce timer to batch rapid saves.
    /// Does NOT watch engine source (too large, rarely changes).
    /// </summary>
    internal sealed class IncrementalIndexer : IDisposable
    {
        private readonly UnrealIndexer _indexer;
        private readonly CacheService _cache;
        private FileSystemWatcher _watcher;
        private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new ConcurrentDictionary<string, DateTime>();
        private Timer _debounceTimer;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        private const int DebounceMs = 500;

        public IncrementalIndexer(UnrealIndexer indexer, CacheService cache)
        {
            _indexer = indexer;
            _cache = cache;
        }

        /// <summary>
        /// Starts watching <paramref name="projectSourceRoot"/> for .h/.cpp file changes.
        /// </summary>
        public void Start(string projectSourceRoot)
        {
            if (string.IsNullOrEmpty(projectSourceRoot) || !Directory.Exists(projectSourceRoot))
                return;

            _watcher = new FileSystemWatcher(projectSourceRoot)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            // Watch C++ source files
            _watcher.Filter = "*.*"; // We filter in the handler

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Deleted += OnFileDeleted;

            _debounceTimer = new Timer(ProcessPendingChanges, null, Timeout.Infinite, Timeout.Infinite);
        }

        // ── Event handlers ──────────────────────────────────────────

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!IsSourceFile(e.FullPath)) return;

            _pendingChanges[e.FullPath] = DateTime.UtcNow;
            RestartDebounceTimer();
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            // Treat rename as delete old + create new
            if (IsSourceFile(e.OldFullPath))
            {
                Task.Run(() => _cache.RemoveFileSymbolsAsync(e.OldFullPath));
            }

            if (IsSourceFile(e.FullPath))
            {
                _pendingChanges[e.FullPath] = DateTime.UtcNow;
                RestartDebounceTimer();
            }
        }

        private void OnFileDeleted(object sender, FileSystemEventArgs e)
        {
            if (!IsSourceFile(e.FullPath)) return;

            Task.Run(() => _cache.RemoveFileSymbolsAsync(e.FullPath));
        }

        // ── Debounce processing ─────────────────────────────────────

        private void RestartDebounceTimer()
        {
            _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
        }

        private void ProcessPendingChanges(object state)
        {
            if (_cts.IsCancellationRequested) return;

            // Snapshot and clear pending changes
            var files = _pendingChanges.Keys.ToArray();
            foreach (var f in files)
                _pendingChanges.TryRemove(f, out _);

            // Re-index each changed file
            Task.Run(async () =>
            {
                foreach (var filePath in files)
                {
                    if (_cts.IsCancellationRequested) break;

                    try
                    {
                        await _indexer.IndexSingleFileAsync(filePath, isEngine: false, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { /* log and continue */ }
                }
            });
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static bool IsSourceFile(string path)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;

            return ext.Equals(".h", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".cpp", StringComparison.OrdinalIgnoreCase);
        }

        // ── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _debounceTimer?.Dispose();

            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileChanged;
                _watcher.Created -= OnFileChanged;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Deleted -= OnFileDeleted;
                _watcher.Dispose();
            }

            _cts.Dispose();
        }
    }
}
