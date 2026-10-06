using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PenguinExtention.Core
{
    internal sealed class CoreFileWatcher : IDisposable
    {
        private readonly CoreClientService core;
        private readonly ConcurrentDictionary<string, int> pending = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly FileSystemWatcher watcher;
        public CoreFileWatcher(CoreClientService core, string root)
        {
            this.core = core;
            if (!Directory.Exists(root)) return;
            watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, Filter = "*.*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
            watcher.Changed += (s,e) => Add(e.FullPath, 2);
            watcher.Created += (s,e) => Add(e.FullPath, 1);
            watcher.Deleted += (s,e) => Add(e.FullPath, 3);
            watcher.Renamed += (s,e) => { Add(e.OldFullPath, 3); Add(e.FullPath, 1); };
            watcher.Error += (s,e) => BackendService.Report("Core watcher overflow/error. Use Reindex to refresh disk symbols.");
            watcher.EnableRaisingEvents = true;
            _ = BackendService.ObserveAsync(Task.Run(DrainAsync));
        }
        private void Add(string path, int type)
        {
            var ext = Path.GetExtension(path);
            if (!ext.Equals(".h", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase)) return;
            var normalized = path.Replace((char)92, '/');
            if (normalized.IndexOf("/Intermediate/", StringComparison.OrdinalIgnoreCase) >= 0 || normalized.IndexOf("/Binaries/", StringComparison.OrdinalIgnoreCase) >= 0) return;
            if (pending.Count >= 2048) { BackendService.Report("Core watcher backlog full. Use Reindex."); return; }
            pending[path] = type;
        }
        private async Task DrainAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(500, stop.Token).ConfigureAwait(false);
                var changes = pending.Keys.Take(256).Select(path =>
                {
                    int type;
                    return pending.TryRemove(path, out type) ? (object)new { uri = Protocol.FileUri(path), type } : null;
                }).Where(x => x != null).ToArray();
                if (changes.Length > 0) await core.FilesChangedAsync(changes).ConfigureAwait(false);
            }
        }
        public void Dispose() { stop.Cancel(); watcher?.Dispose(); }
    }
}
