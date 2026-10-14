using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Commands;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Editor
{
    /// <summary>
    /// Watches typing in C/C++ views: on '(' after an indexed Unreal function name it looks the symbol up
    /// off the UI thread, then opens (or joins) a signature-help session with the declaration and docs.
    /// Writing an implementation ("void AMyActor::Foo(") shows the header declaration the same way.
    /// </summary>
    [Export(typeof(IIntellisenseControllerProvider))]
    [Name("Penguin Unreal Signature Help Controller")]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class UnrealSignatureHelpControllerProvider : IIntellisenseControllerProvider
    {
        [Import] internal ISignatureHelpBroker Broker = null;

        public IIntellisenseController TryCreateIntellisenseController(ITextView textView, IList<ITextBuffer> subjectBuffers) =>
            new UnrealSignatureHelpController(textView, Broker);
    }

    internal sealed class UnrealSignatureHelpController : IIntellisenseController
    {
        internal const string PendingKey = "PenguinSignatureHelpPending";
        private ITextView _view;
        private readonly ISignatureHelpBroker _broker;
        private CancellationTokenSource _lookup;

        public UnrealSignatureHelpController(ITextView view, ISignatureHelpBroker broker)
        {
            _view = view;
            _broker = broker;
            _view.TextBuffer.PostChanged += OnPostChanged;
        }

        internal const string ProbeLookup = "Signature help (lookup)";
        internal const string ProbeSource = "Signature help (VS asked our source)";

        private void OnPostChanged(object sender, EventArgs e)
        {
            if (PenguinOptionsPage.Current?.EnableSignatureHelp == false || PenguinOptionsPage.Current?.ShowParameterInfoBox != false || !BackendService.IsReady) return;
            var snapshot = _view.TextBuffer.CurrentSnapshot;
            var caret = _view.Caret.Position.BufferPosition;
            if (caret.Snapshot != snapshot || caret.Position == 0) return;
            char typed = snapshot[caret.Position - 1];
            if (typed != '(' && typed != ',') return;

            int open = typed == '(' ? caret.Position - 1 : FindOpenParen(snapshot, caret.Position - 1);
            if (open < 0) return;
            var line = snapshot.GetLineFromPosition(open);
            var name = OccurrenceScanner.IdentifierAt(line.GetText(), open - line.Start.Position, out int start);
            if (name == null || start + name.Length != TrimmedEnd(line.GetText(), open - line.Start.Position)) return;
            var nameSpan = new SnapshotSpan(line.Start + start, name.Length);
            var qualifier = PenguinEditor.QualifierBefore(nameSpan);
            var file = PenguinEditor.FilePathOf(_view);

            _lookup?.Cancel();
            _lookup = new CancellationTokenSource();
            var token = _lookup.Token;
            var applicable = snapshot.CreateTrackingSpan(new Span(open, caret.Position - open), SpanTrackingMode.EdgeInclusive);

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await TaskScheduler.Default;
                    var candidates = (await BackendService.ExactAsync(name, token).ConfigureAwait(false))
                        .Where(s => s.Kind == UnrealSymbolKind.Function || s.Kind == UnrealSymbolKind.Delegate).ToList();
                    if (qualifier != null && candidates.Any(s => s.OwnerClass == qualifier))
                        candidates = candidates.Where(s => s.OwnerClass == qualifier).ToList();
                    FeatureProbe.Mark(ProbeLookup, name + " → " + candidates.Count + " overload(s)");
                    if (candidates.Count == 0) return;
                    var partner = file == null ? null : HeaderSourceResolver.Partner(file);
                    var ordered = candidates
                        .OrderBy(s => PenguinEditor.SameFile(s.FilePath, file) || PenguinEditor.SameFile(s.FilePath, partner) ? 0 : 1)
                        .ThenBy(s => s.IsEngineSymbol ? 1 : 0)
                        .ThenByDescending(s => s.AccessCount).Take(12).ToList();

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (_view == null || _view.IsClosed) return;
                    _view.Properties[PendingKey] = new PendingSignatures { Symbols = ordered, ApplicableTo = applicable };
                    if (_broker.IsSignatureHelpActive(_view))
                        foreach (var session in _broker.GetSessions(_view)) session.Recalculate();
                    else
                        _broker.TriggerSignatureHelp(_view);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PenguinLog.Error("SignatureHelp", ex); }
            });
        }

        private static int TrimmedEnd(string text, int end)
        {
            while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
            return end;
        }

        /// <summary>Unmatched '(' before <paramref name="from"/> on the same statement (bounded look-back).</summary>
        internal static int FindOpenParen(ITextSnapshot snapshot, int from)
        {
            int depth = 0;
            for (int i = from; i >= 0 && from - i < 2000; i--)
            {
                char c = snapshot[i];
                if (c == ')') depth++;
                else if (c == '(') { if (depth == 0) return i; depth--; }
                else if (c == ';' || c == '{' || c == '}') return -1;
            }
            return -1;
        }

        public void ConnectSubjectBuffer(ITextBuffer subjectBuffer) { }
        public void DisconnectSubjectBuffer(ITextBuffer subjectBuffer) { }

        public void Detach(ITextView textView)
        {
            if (_view != textView) return;
            _lookup?.Cancel();
            _view.TextBuffer.PostChanged -= OnPostChanged;
            _view = null;
        }
    }

    internal sealed class PendingSignatures
    {
        public List<UnrealSymbol> Symbols;
        public ITrackingSpan ApplicableTo;
    }

    [Export(typeof(ISignatureHelpSourceProvider))]
    [Name("Penguin Unreal Signature Help Source")]
    [ContentType("C/C++")]
    [Order(Before = "default")]
    internal sealed class UnrealSignatureHelpSourceProvider : ISignatureHelpSourceProvider
    {
        public ISignatureHelpSource TryCreateSignatureHelpSource(ITextBuffer textBuffer) => new UnrealSignatureHelpSource(textBuffer);
    }

    internal sealed class UnrealSignatureHelpSource : ISignatureHelpSource
    {
        private readonly ITextBuffer _buffer;
        public UnrealSignatureHelpSource(ITextBuffer buffer) { _buffer = buffer; }

        public void AugmentSignatureHelpSession(ISignatureHelpSession session, IList<ISignature> signatures)
        {
            FeatureProbe.Mark(UnrealSignatureHelpController.ProbeSource);
            // Only contribute what the controller resolved for this view; never query on the UI thread here.
            if (!session.TextView.Properties.TryGetProperty(UnrealSignatureHelpController.PendingKey, out PendingSignatures pending)) return;
            if (pending.ApplicableTo.TextBuffer != _buffer) return;
            // Ignore a lookup made for a different call than the one the caret is in now.
            var snapshot = _buffer.CurrentSnapshot;
            var caret = session.TextView.Caret.Position.BufferPosition;
            if (caret.Snapshot != snapshot) return;
            int open = UnrealSignatureHelpController.FindOpenParen(snapshot, Math.Max(0, caret.Position - 1));
            if (open < 0 || open != pending.ApplicableTo.GetStartPoint(snapshot).Position) return;
            foreach (var symbol in pending.Symbols)
                signatures.Add(new UnrealSignature(_buffer, symbol, pending.ApplicableTo, session));
        }

        public ISignature GetBestMatch(ISignatureHelpSession session) => null;
        public void Dispose() { }
    }

    internal sealed class UnrealSignature : ISignature
    {
        private readonly ITextBuffer _buffer;
        private readonly ISignatureHelpSession _session;
        private IParameter _current;

        public UnrealSignature(ITextBuffer buffer, UnrealSymbol symbol, ITrackingSpan applicableTo, ISignatureHelpSession session)
        {
            _buffer = buffer;
            _session = session;
            ApplicableToSpan = applicableTo;

            var signature = string.IsNullOrWhiteSpace(symbol.Signature)
                ? (symbol.ReturnType ?? "") + " " + symbol.DisplayText + "()"
                : symbol.Signature.Trim();
            if (!string.IsNullOrEmpty(symbol.OwnerClass) && signature.IndexOf(symbol.OwnerClass + "::", StringComparison.Ordinal) < 0)
            {
                int nameIdx = IndexOfName(signature, symbol.Name);
                if (nameIdx >= 0) signature = signature.Insert(nameIdx, symbol.OwnerClass + "::");
            }
            Content = signature;
            PrettyPrintedContent = signature;

            var doc = new List<string>();
            if (!string.IsNullOrWhiteSpace(symbol.Comment)) doc.Add(symbol.Comment.Trim());
            if (!string.IsNullOrWhiteSpace(symbol.Specifiers)) doc.Add((symbol.MacroName ?? "UFUNCTION") + "(" + symbol.Specifiers + ")");
            doc.Add("Declared in " + System.IO.Path.GetFileName(symbol.FilePath) + ":" + symbol.LineNumber + (symbol.IsEngineSymbol ? " (engine)" : ""));
            Documentation = string.Join(Environment.NewLine, doc);

            Parameters = new ReadOnlyCollection<IParameter>(ParseParameters(signature));
            ComputeCurrentParameter();
            _buffer.Changed += OnBufferChanged;
            session.Dismissed += (s, e) => _buffer.Changed -= OnBufferChanged;
        }

        public ITrackingSpan ApplicableToSpan { get; }
        public string Content { get; }
        public string PrettyPrintedContent { get; }
        public string Documentation { get; }
        public ReadOnlyCollection<IParameter> Parameters { get; }

        public IParameter CurrentParameter
        {
            get => _current;
            private set
            {
                if (_current == value) return;
                var old = _current;
                _current = value;
                CurrentParameterChanged?.Invoke(this, new CurrentParameterChangedEventArgs(old, value));
            }
        }

        public event EventHandler<CurrentParameterChangedEventArgs> CurrentParameterChanged;

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            var span = ApplicableToSpan.GetSpan(e.After);
            var caret = _session.TextView.Caret.Position.BufferPosition.Position;
            // Dismiss once the call's closing ')' is typed or the caret leaves the argument list.
            if (caret < span.Start.Position || caret > span.End.Position + 1 ||
                e.Changes.Any(c => c.NewText == ")") && UnrealSignatureHelpController.FindOpenParen(e.After, Math.Max(0, caret - 1)) != span.Start.Position)
            {
                if (!_session.IsDismissed) _session.Dismiss();
                return;
            }
            ComputeCurrentParameter();
        }

        private void ComputeCurrentParameter()
        {
            if (Parameters.Count == 0) { CurrentParameter = null; return; }
            var snapshot = _buffer.CurrentSnapshot;
            var span = ApplicableToSpan.GetSpan(snapshot);
            var text = span.GetText();
            int depth = 0, commas = 0;
            for (int i = 1; i < text.Length; i++) // text[0] is the '('
            {
                char c = text[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0) commas++;
            }
            CurrentParameter = Parameters[Math.Min(commas, Parameters.Count - 1)];
        }

        private List<IParameter> ParseParameters(string signature)
        {
            var result = new List<IParameter>();
            int open = signature.IndexOf('(');
            if (open < 0) return result;
            int depth = 0, start = open + 1;
            for (int i = open; i < signature.Length; i++)
            {
                char c = signature[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0) { Add(result, signature, start, i); break; }
                }
                else if (c == ',' && depth == 1) { Add(result, signature, start, i); start = i + 1; }
            }
            return result;
        }

        private void Add(List<IParameter> list, string signature, int start, int end)
        {
            var raw = signature.Substring(start, end - start);
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed == "void") return;
            int lead = raw.IndexOf(trimmed, StringComparison.Ordinal);
            var locus = new Span(start + lead, trimmed.Length);
            int eq = trimmed.IndexOf('=');
            var decl = (eq >= 0 ? trimmed.Substring(0, eq) : trimmed).Trim();
            int n = decl.Length;
            while (n > 0 && OccurrenceScanner.IsIdentPart(decl[n - 1])) n--;
            var name = n < decl.Length && n > 0 ? decl.Substring(n) : decl;
            list.Add(new UnrealParameter(this, name, trimmed, locus));
        }

        private static int IndexOfName(string signature, string name)
        {
            int open = signature.IndexOf('(');
            int idx = open < 0 ? signature.LastIndexOf(name, StringComparison.Ordinal) : signature.LastIndexOf(name, open, StringComparison.Ordinal);
            return idx;
        }
    }

    internal sealed class UnrealParameter : IParameter
    {
        public UnrealParameter(ISignature signature, string name, string documentation, Span locus)
        {
            Signature = signature; Name = name; Documentation = documentation; Locus = locus; PrettyPrintedLocus = locus;
        }

        public ISignature Signature { get; }
        public string Name { get; }
        public string Documentation { get; }
        public Span Locus { get; }
        public Span PrettyPrintedLocus { get; }
    }
}
