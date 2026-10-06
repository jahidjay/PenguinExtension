using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace PenguinExtention.Core
{
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class DocumentListener : IWpfTextViewCreationListener
    {
        [Import] internal ITextDocumentFactoryService Documents { get; set; }
        public void TextViewCreated(IWpfTextView textView)
        {
            ITextDocument document;
            if (Documents.TryGetTextDocument(textView.TextBuffer, out document))
            {
                DocumentSync.Attach(document);
                textView.Closed += (s, e) => DocumentSync.Detach(document);
            }
        }
    }

    internal static class DocumentSync
    {
        private sealed class Entry
        {
            public ITextDocument Document;
            public string Uri;
            public long Version;
            public int Views;
            public ITextSnapshot Snapshot;
        }
        private static readonly object gate = new object();
        private static readonly Dictionary<ITextBuffer, Entry> entries = new Dictionary<ITextBuffer, Entry>();
        private static long nextVersion;
        public static event Action<string> DocumentInvalidated;

        public static void Attach(ITextDocument document)
        {
            lock (gate)
            {
                Entry entry;
                if (entries.TryGetValue(document.TextBuffer, out entry)) { entry.Views++; return; }
                entry = new Entry { Document = document, Uri = Protocol.FileUri(document.FilePath), Views = 1,
                    Snapshot = document.TextBuffer.CurrentSnapshot, Version = Interlocked.Increment(ref nextVersion) };
                entries.Add(document.TextBuffer, entry);
                document.TextBuffer.Changed += Changed;
                document.FileActionOccurred += FileAction;
                Send(entry);
            }
        }
        public static void Detach(ITextDocument document)
        {
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(document.TextBuffer, out entry) || --entry.Views > 0) return;
                entries.Remove(document.TextBuffer);
                document.TextBuffer.Changed -= Changed;
                document.FileActionOccurred -= FileAction;
                var core = BackendService.Core;
                if (core?.IsReady == true) _ = BackendService.ObserveAsync(core.CloseDocumentAsync(entry.Uri));
                DocumentInvalidated?.Invoke(entry.Uri);
            }
        }
        private static void Changed(object sender, TextContentChangedEventArgs e)
        {
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue((ITextBuffer)sender, out entry)) return;
                entry.Snapshot = e.After;
                entry.Version = Interlocked.Increment(ref nextVersion);
                DocumentInvalidated?.Invoke(entry.Uri);
                Send(entry);
            }
        }
        private static void FileAction(object sender, TextDocumentFileActionEventArgs e)
        {
            lock (gate)
            {
                var doc = (ITextDocument)sender;
                Entry entry;
                if (!entries.TryGetValue(doc.TextBuffer, out entry)) return;
                var uri = Protocol.FileUri(doc.FilePath);
                if (uri != entry.Uri)
                {
                    var core = BackendService.Core;
                    if (core?.IsReady == true) _ = BackendService.ObserveAsync(core.CloseDocumentAsync(entry.Uri));
                    DocumentInvalidated?.Invoke(entry.Uri);
                    entry.Uri = uri; entry.Version = Interlocked.Increment(ref nextVersion);
                }
                entry.Snapshot = doc.TextBuffer.CurrentSnapshot;
                Send(entry, (e.FileActionType & FileActionTypes.ContentSavedToDisk) != 0);
            }
        }
        private static void Send(Entry entry, bool save = false)
        {
            var core = BackendService.Core;
            var snapshot = entry.Snapshot;
            if (core?.IsReady == true)
                _ = BackendService.ObserveAsync(core.SynchronizeAsync(entry.Uri, entry.Version, snapshot.GetText, save));
        }
        public static void Replay()
        {
            lock (gate)
                foreach (var entry in entries.Values)
                {
                    entry.Version = Interlocked.Increment(ref nextVersion);
                    Send(entry);
                }
        }
        public static bool IsCurrent(string uri, long? version)
        {
            lock (gate) return entries.Values.Any(e => e.Uri == uri && (!version.HasValue || e.Version == version));
        }
        public static async Task<JToken> RequestAsync(string method, SnapshotPoint point, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            Task<JToken> request;
            var generation = BackendService.Generation;
            lock (gate)
            {
                Entry entry;
                if (!entries.TryGetValue(point.Snapshot.TextBuffer, out entry)) return null;
                if (point.Snapshot != entry.Document.TextBuffer.CurrentSnapshot) throw new OperationCanceledException();
                var core = BackendService.Core;
                if (core == null) return null;
                Send(entry);
                var line = point.GetContainingLine();
                request = core.RequestAsync(method, new { textDocument = new { uri = entry.Uri },
                    position = new { line = line.LineNumber, character = point.Position - line.Start.Position } }, token);
            }
            var result = await request.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != BackendService.Generation || point.Snapshot != point.Snapshot.TextBuffer.CurrentSnapshot)
                throw new OperationCanceledException();
            return result;
        }
    }
}
