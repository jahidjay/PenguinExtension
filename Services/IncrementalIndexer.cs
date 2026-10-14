using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PenguinExtention.Services
{
    // A single drainable worker owns incremental legacy writes. Dispose never races DB shutdown.
    internal sealed class IncrementalIndexer : IDisposable
    {
        private readonly UnrealIndexer _indexer;
        private readonly CacheService _cache;
        private readonly ConcurrentDictionary<string, bool> _pending = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private FileSystemWatcher _watcher;
        private string _root;
        private Task _worker = Task.CompletedTask;
        private bool _disposed;
        public IncrementalIndexer(UnrealIndexer indexer, CacheService cache) { _indexer = indexer; _cache = cache; }
        public void Start(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            _root = root;
            _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, Filter = "*.*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
            _watcher.Changed += Changed; _watcher.Created += Changed; _watcher.Deleted += Deleted; _watcher.Renamed += Renamed;
            _worker = Task.Run(DrainAsync);
            _watcher.EnableRaisingEvents = true;
        }
        private static bool Source(string path)
        {
            return UnrealIndexer.SourceExtensions.Contains(Path.GetExtension(path)) &&
                   path.IndexOf(Path.DirectorySeparatorChar + ".vs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0;
        }
        private void Changed(object s, FileSystemEventArgs e) { if (Source(e.FullPath)) _pending[e.FullPath] = false; }
        private void Deleted(object s, FileSystemEventArgs e) { if (Source(e.FullPath)) _pending[e.FullPath] = true; }
        private void Renamed(object s, RenamedEventArgs e)
        {
            if (Source(e.OldFullPath)) _pending[e.OldFullPath] = true;
            if (Source(e.FullPath)) _pending[e.FullPath] = false;
        }
        private async Task DrainAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(500, _cts.Token).ConfigureAwait(false);
                    if (_pending.IsEmpty) continue;

                    _cache.GetKnownNames(out var namesBefore);
                    var changed = new List<string>();
                    foreach (var path in _pending.Keys)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        bool deleted;
                        if (!_pending.TryRemove(path, out deleted)) continue;
                        try
                        {
                            if (deleted) await _cache.RemoveFileSymbolsAsync(path).ConfigureAwait(false);
                            else
                            {
                                await _indexer.IndexSingleFileAsync(path, false, _cts.Token).ConfigureAwait(false);
                                changed.Add(path);
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
                    }

                    // A changed name set can create references in untouched files; otherwise rescan only edited files.
                    try
                    {
                        _cache.GetKnownNames(out var namesAfter);
                        if (namesAfter != namesBefore)
                            await _indexer.ScanOccurrencesAsync(_root, _cts.Token).ConfigureAwait(false);
                        else
                            foreach (var path in changed)
                                await _indexer.ScanFileOccurrencesAsync(path, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); }
                }
            }
            catch (OperationCanceledException) { }
        }
        public async Task StopAsync() { Dispose(); await _worker.ConfigureAwait(false); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; _cts.Cancel(); _watcher?.Dispose();
        }
    }
}
