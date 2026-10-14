using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Services;

namespace PenguinExtention.Editor
{
    [Export(typeof(EditorFormatDefinition))]
    [Name(HighlightReferenceTag.FormatName)]
    [UserVisible(true)]
    internal sealed class HighlightReferenceFormat : MarkerFormatDefinition
    {
        public HighlightReferenceFormat()
        {
            DisplayName = "Penguin Reference Highlight";
            Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x5A, 0x9B, 0xD5));
            Border = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x5A, 0x9B, 0xD5)), 1);
            Fill.Freeze(); Border.Freeze();
            ZOrder = 5;
        }
    }

    internal sealed class HighlightReferenceTag : TextMarkerTag
    {
        public const string FormatName = "Penguin/HighlightReference";
        public HighlightReferenceTag() : base(FormatName) { }
    }

    [Export(typeof(IViewTaggerProvider))]
    [ContentType("C/C++")]
    [TagType(typeof(TextMarkerTag))]
    internal sealed class HighlightReferencesTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (textView.TextBuffer != buffer) return null;
            return textView.Properties.GetOrCreateSingletonProperty(() => new HighlightReferencesTagger(textView)) as ITagger<T>;
        }
    }

    /// <summary>
    /// Highlights other occurrences of the identifier under the caret (comment/string-aware, whole-word).
    /// Debounced 250 ms after the caret settles; the scan runs on a background thread.
    /// </summary>
    internal sealed class HighlightReferencesTagger : ITagger<TextMarkerTag>
    {
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "while", "do", "switch", "case", "default", "return", "break", "continue", "class", "struct",
            "enum", "public", "private", "protected", "virtual", "override", "final", "const", "static", "void", "bool", "int",
            "float", "double", "char", "auto", "true", "false", "nullptr", "this", "new", "delete", "template", "typename",
            "namespace", "using", "inline", "operator", "sizeof", "constexpr", "unsigned", "signed", "long", "short"
        };

        private readonly ITextView _view;
        private readonly DispatcherTimer _timer;
        private NormalizedSnapshotSpanCollection _spans;
        private CancellationTokenSource _scan;
        private string _word;

        public HighlightReferencesTagger(ITextView view)
        {
            _view = view;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => { _timer.Stop(); Update(); };
            _view.Caret.PositionChanged += (s, e) => Restart();
            _view.TextBuffer.Changed += (s, e) => { Clear(); Restart(); };
            _view.Closed += (s, e) => { _timer.Stop(); _scan?.Cancel(); };
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        private void Restart()
        {
            _timer.Stop();
            _timer.Start();
        }

        private void Update()
        {
            if (PenguinOptionsPage.Current?.EnableReferenceHighlight != true || _view.IsClosed) { Clear(); return; }
            var caret = _view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            var word = OccurrenceScanner.IdentifierAt(line.GetText(), caret.Position - line.Start.Position, out _);
            if (word == null || word.Length < 2 || Keywords.Contains(word)) { Clear(); return; }
            if (word == _word && _spans != null && _spans.Count > 0 && _spans[0].Snapshot == caret.Snapshot) return;

            _word = word;
            _scan?.Cancel();
            _scan = new CancellationTokenSource();
            var token = _scan.Token;
            var snapshot = caret.Snapshot;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                if (snapshot.Length > 4_000_000) return; // huge generated files
                var text = snapshot.GetText();
                var hits = OccurrenceScanner.ScanFor(text, word);
                if (token.IsCancellationRequested || hits.Count < 2) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); if (!token.IsCancellationRequested) Clear(); return; }
                var spans = new NormalizedSnapshotSpanCollection(hits.Select(h =>
                {
                    var l = snapshot.GetLineFromLineNumber(h.LineNumber - 1);
                    return new SnapshotSpan(l.Start + h.ColumnNumber, word.Length);
                }));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (token.IsCancellationRequested || _view.IsClosed || _view.TextSnapshot != snapshot) return;
                _spans = spans;
                Raise(snapshot);
            });
        }

        private void Clear()
        {
            if (_spans == null || _spans.Count == 0) { _spans = null; return; }
            var snapshot = _spans[0].Snapshot;
            _spans = null;
            _word = null;
            Raise(snapshot);
        }

        private void Raise(ITextSnapshot snapshot) =>
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));

        public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var current = _spans;
            if (current == null || current.Count == 0 || spans.Count == 0) yield break;
            var target = spans[0].Snapshot;
            if (current[0].Snapshot != target) current = new NormalizedSnapshotSpanCollection(current.Select(s => s.TranslateTo(target, SpanTrackingMode.EdgeExclusive)));
            foreach (var s in NormalizedSnapshotSpanCollection.Overlap(current, spans))
                yield return new TagSpan<TextMarkerTag>(s, new HighlightReferenceTag());
        }
    }
}
