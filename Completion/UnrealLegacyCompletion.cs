using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Unreal completion for the C/C++ editor's *legacy* completion pipeline — the one VC++ actually uses
    /// (Microsoft.VisualStudio.VC.VCCompletionSource implements ICompletionSource). The async source in
    /// UnrealCompletionSource is never consulted for C/C++ sessions, so this source is what users see:
    ///  • inside UPROPERTY/UFUNCTION/UCLASS/USTRUCT/UENUM( … ) — a "🐧 Unreal Specifiers" set, selected first;
    ///  • on identifiers — a "🐧 Unreal" set (index + acronym matches) beside Visual Studio's own list.
    /// Augmentation is synchronous by contract, so it only reads the in-memory Legacy cache (never blocks on I/O).
    /// </summary>
    [Export(typeof(ICompletionSourceProvider))]
    [ContentType("C/C++")]
    [Name("Penguin Unreal Completion (legacy pipeline)")]
    internal sealed class UnrealLegacyCompletionSourceProvider : ICompletionSourceProvider
    {
        [Import] internal IGlyphService Glyphs = null;

        public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(() => new UnrealLegacyCompletionSource(textBuffer, Glyphs));
    }

    internal sealed class UnrealLegacyCompletionSource : ICompletionSource
    {
        internal const string SpecifierSetName = "🐧 Unreal Specifiers";
        internal const string SymbolSetName = "🐧 Unreal";
        private readonly ITextBuffer _buffer;
        private readonly IGlyphService _glyphs;
        private bool _disposed;

        public UnrealLegacyCompletionSource(ITextBuffer buffer, IGlyphService glyphs)
        {
            _buffer = buffer;
            _glyphs = glyphs;
        }

        internal const string ProbeLegacy = "Completion (legacy pipeline)";

        public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
        {
            if (_disposed || !BackendService.CompletionEnabled) return;
            FeatureProbe.Mark(ProbeLegacy);
            try { Augment(session, completionSets); }
            catch (Exception ex) { PenguinLog.Error("Completion", ex); }
        }

        private void Augment(ICompletionSession session, IList<CompletionSet> completionSets)
        {
            var snapshot = _buffer.CurrentSnapshot;
            var trigger = session.GetTriggerPoint(snapshot);
            if (!trigger.HasValue) return;
            var point = trigger.Value;
            if (completionSets.Any(s => s.Moniker == SpecifierSetName || s.Moniker == SymbolSetName)) return;

            // ── Specifiers inside a reflection macro ────────────────
            var macro = UnrealCompletionSource.DetectMacroContext(snapshot, point);
            if (macro != null)
            {
                var set = BuildSpecifierSet(snapshot, point, macro.Value.macroName, macro.Value.existingText);
                if (set != null)
                {
                    completionSets.Insert(0, set);
                    Select(session, set);
                }
                return;
            }

            // ── Indexed Unreal symbols ──────────────────────────────
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
            {
                PenguinLog.Info("Completion", "index not loaded yet — no Unreal suggestions");
                return;
            }

            var line = point.GetContainingLine();
            var text = line.GetText();
            int col = point.Position - line.Start.Position;
            int start = col;
            while (start > 0 && OccurrenceScanner.IsIdentPart(text[start - 1])) start--;
            var prefix = text.Substring(start, col - start);
            var before = text.Substring(0, start).TrimEnd();
            var applicable = snapshot.CreateTrackingSpan(line.Start.Position + start, col - start, SpanTrackingMode.EdgeInclusive);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            CompletionSet symbolSet;
            if (before.EndsWith("::"))
            {
                // Owner:: → that class's members / enum's values
                var owner = before.Substring(0, before.Length - 2).TrimEnd();
                int os = owner.Length;
                while (os > 0 && OccurrenceScanner.IsIdentPart(owner[os - 1])) os--;
                owner = owner.Substring(os);
                if (owner.Length == 0 || owner == "Super") return;
                var members = cache.GetMembers(owner);
                if (members.Count == 0) return;
                symbolSet = new CompletionSet(SymbolSetName, SymbolSetName, applicable, ToCompletions(members, ""), null);
                PenguinLog.Info("Completion", "legacy \"" + owner + "::\" → " + members.Count + " member(s)");
            }
            else
            {
                if (prefix.Length < 1 || before.EndsWith(".") || before.EndsWith("->")) return; // member access: VC++ knows the type
                var set = new RequeryingSymbolSet(this, applicable, prefix);
                if (set.Completions.Count == 0) return;
                symbolSet = set;
                PenguinLog.Info("Completion", "legacy \"" + prefix + "\" → " + set.Completions.Count + " item(s) in " + watch.ElapsedMilliseconds + " ms");
            }
            completionSets.Add(symbolSet);
            // Keep Visual Studio's own list selected for ordinary typing; Owner:: lists lead with ours.
            if (before.EndsWith("::")) Select(session, symbolSet);
        }

        private const int MaxItems = 500;

        private List<Microsoft.VisualStudio.Language.Intellisense.Completion> ToCompletions(IEnumerable<UnrealSymbol> symbols, string prefix) =>
            symbols
                .GroupBy(s => s.Name).Select(g => g.OrderBy(s => s.IsEngineSymbol ? 1 : 0).First())
                .OrderBy(s => s.Name.StartsWith(prefix, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(s => s.IsEngineSymbol ? 1 : 0)
                .ThenByDescending(s => s.AccessCount)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(s => (Microsoft.VisualStudio.Language.Intellisense.Completion)new LazyCompletion(s, Icon(s.Kind)))
                .ToList();

        /// <summary>
        /// The "🐧 Unreal" set. When the first query was capped, typing more characters re-queries the index
        /// (instead of only filtering the first 500 "U…" names), so long names like UCharacterMovementComponent are found.
        /// </summary>
        private sealed class RequeryingSymbolSet : CompletionSet
        {
            private readonly UnrealLegacyCompletionSource _source;
            private string _queried;
            private bool _capped;

            public RequeryingSymbolSet(UnrealLegacyCompletionSource source, ITrackingSpan applicable, string prefix)
                : base(SymbolSetName, SymbolSetName, applicable, Enumerable.Empty<Microsoft.VisualStudio.Language.Intellisense.Completion>(), null)
            {
                _source = source;
                Load(prefix);
            }

            private void Load(string prefix)
            {
                var symbols = CacheService.Instance?.Search(prefix, null, MaxItems) ?? new List<UnrealSymbol>();
                _queried = prefix;
                _capped = symbols.Count >= MaxItems;
                var list = _source.ToCompletions(symbols, prefix);
                WritableCompletions.BeginBulkOperation();
                try { WritableCompletions.Clear(); foreach (var c in list) WritableCompletions.Add(c); }
                finally { WritableCompletions.EndBulkOperation(); }
            }

            public override void Recalculate()
            {
                try
                {
                    var typed = ApplicableTo.GetText(ApplicableTo.TextBuffer.CurrentSnapshot);
                    if (_capped && typed.Length > _queried.Length && typed.StartsWith(_queried, StringComparison.OrdinalIgnoreCase))
                        Load(typed);
                }
                catch (Exception ex) { PenguinLog.Error("Completion", ex); }
                base.Recalculate();
            }
        }

        private CompletionSet BuildSpecifierSet(ITextSnapshot snapshot, SnapshotPoint point, string macroName, string existingText)
        {
            var specifiers = UnrealMacroSpecifiers.GetSpecifiers(macroName);
            if (specifiers == null || specifiers.Length == 0) return null;

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = existingText.Split(',');
            foreach (var part in parts.Take(parts.Length - 1)) // the last part is the token being typed
            {
                var t = part.Trim();
                int cut = t.IndexOfAny(new[] { '=', '(' });
                var name = (cut >= 0 ? t.Substring(0, cut) : t).Trim();
                if (name.Length > 0) used.Add(name);
            }

            // Span of the specifier token being typed (letters/digits/underscore before the caret).
            var line = point.GetContainingLine();
            var text = line.GetText();
            int col = point.Position - line.Start.Position, start = col;
            while (start > 0 && OccurrenceScanner.IsIdentPart(text[start - 1])) start--;
            var typed = text.Substring(start, col - start);
            used.Remove(typed); // the token being typed isn't "already used"

            var icon = _glyphs?.GetGlyph(StandardGlyphGroup.GlyphKeyword, StandardGlyphItem.GlyphItemPublic);
            var completions = specifiers
                .Where(s => !used.Contains(s.Name) || s.Name == "meta")
                .Select(s => new Microsoft.VisualStudio.Language.Intellisense.Completion(s.Name, s.InsertText ?? s.Name, macroName + " specifier\n\n" + s.Description, icon, s.Name))
                .ToList();
            if (completions.Count == 0) return null;
            PenguinLog.Info("Completion", "legacy " + macroName + "( specifiers → " + completions.Count);
            var applicable = snapshot.CreateTrackingSpan(line.Start.Position + start, col - start, SpanTrackingMode.EdgeInclusive);
            return new CompletionSet(SpecifierSetName, SpecifierSetName, applicable, completions, null);
        }

        /// <summary>Selects <paramref name="set"/> once the broker has finished collecting all sources.</summary>
        private static void Select(ICompletionSession session, CompletionSet set) =>
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (!session.IsDismissed && session.CompletionSets.Contains(set)) session.SelectedCompletionSet = set;
            }), DispatcherPriority.Background);

        /// <summary>Builds the tooltip only when VS shows it (inheritance lookup is too costly for 150 items up front).</summary>
        private sealed class LazyCompletion : Microsoft.VisualStudio.Language.Intellisense.Completion
        {
            private readonly UnrealSymbol _symbol;
            private string _description;

            public LazyCompletion(UnrealSymbol symbol, ImageSource icon)
                : base(symbol.Name, symbol.Name, null, icon, symbol.KindDisplay) { _symbol = symbol; }

            public override string Description
            {
                get => _description ?? (_description = SymbolTooltip.PlainText(_symbol));
                set => _description = value;
            }
        }

        private ImageSource Icon(UnrealSymbolKind kind)
        {
            if (_glyphs == null) return null;
            StandardGlyphGroup group;
            switch (kind)
            {
                case UnrealSymbolKind.Class: group = StandardGlyphGroup.GlyphGroupClass; break;
                case UnrealSymbolKind.Struct: group = StandardGlyphGroup.GlyphGroupStruct; break;
                case UnrealSymbolKind.Interface: group = StandardGlyphGroup.GlyphGroupInterface; break;
                case UnrealSymbolKind.Enum: group = StandardGlyphGroup.GlyphGroupEnum; break;
                case UnrealSymbolKind.EnumValue: group = StandardGlyphGroup.GlyphGroupEnumMember; break;
                case UnrealSymbolKind.Function: group = StandardGlyphGroup.GlyphGroupMethod; break;
                case UnrealSymbolKind.Property: group = StandardGlyphGroup.GlyphGroupField; break;
                case UnrealSymbolKind.Variable: group = StandardGlyphGroup.GlyphGroupVariable; break;
                case UnrealSymbolKind.Delegate: group = StandardGlyphGroup.GlyphGroupDelegate; break;
                case UnrealSymbolKind.Macro: group = StandardGlyphGroup.GlyphGroupMacro; break;
                case UnrealSymbolKind.Typedef: group = StandardGlyphGroup.GlyphGroupTypedef; break;
                default: group = StandardGlyphGroup.GlyphGroupUnknown; break;
            }
            return _glyphs.GetGlyph(group, StandardGlyphItem.GlyphItemPublic);
        }

        public void Dispose() => _disposed = true;
    }

    /// <summary>
    /// Opens the completion list inside reflection macros — on '(' and ',' (and the first letter of a
    /// specifier) — where VC++ does not start a session by itself. If a session is already active,
    /// it is restarted so our specifier set is included.
    /// </summary>
    [Export(typeof(IIntellisenseControllerProvider))]
    [Name("Penguin Unreal Specifier Completion Trigger")]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class UnrealSpecifierTriggerProvider : IIntellisenseControllerProvider
    {
        [Import] internal ICompletionBroker Broker = null;

        public IIntellisenseController TryCreateIntellisenseController(ITextView textView, IList<ITextBuffer> subjectBuffers) =>
            new UnrealSpecifierTrigger(textView, Broker);
    }

    internal sealed class UnrealSpecifierTrigger : IIntellisenseController
    {
        private ITextView _view;
        private readonly ICompletionBroker _broker;

        public UnrealSpecifierTrigger(ITextView view, ICompletionBroker broker)
        {
            _view = view;
            _broker = broker;
            _view.TextBuffer.PostChanged += OnPostChanged;
        }

        private void OnPostChanged(object sender, EventArgs e)
        {
            if (_view == null || !BackendService.CompletionEnabled) return;
            // When VS runs the async completion pipeline (VS 2026), the async source opens the specifier list
            // itself; forcing a legacy session as well would show two lists.
            if (FeatureProbe.Seen(UnrealCompletionSource.ProbeAsync)) return;
            if (PenguinOptionsPage.Current?.UsePenguinSuggestionBox != false) return; // the 🐧 box handles specifiers
            var caret = _view.Caret.Position.BufferPosition;
            if (caret.Position == 0 || caret.Snapshot != _view.TextBuffer.CurrentSnapshot) return;
            char typed = caret.Snapshot[caret.Position - 1];
            bool startOfToken = typed == '(' || typed == ',' || typed == ' ' && caret.Position > 1 && caret.Snapshot[caret.Position - 2] == ',' ||
                                char.IsLetter(typed) && caret.Position > 1 && !OccurrenceScanner.IsIdentPart(caret.Snapshot[caret.Position - 2]);
            if (!startOfToken) return;
            if (UnrealCompletionSource.DetectMacroContext(caret.Snapshot, caret) == null) return;

            // Run after the editor finishes processing this keystroke (and after VC++ may have opened its own list).
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
            {
                if (_view == null || _view.IsClosed) return;
                var sessions = _broker.GetSessions(_view);
                if (sessions.Any(s => !s.IsDismissed && s.CompletionSets.Any(c => c.Moniker == UnrealLegacyCompletionSource.SpecifierSetName))) return;
                foreach (var s in sessions.Where(s => !s.IsDismissed).ToList()) s.Dismiss();
                _broker.TriggerCompletion(_view);
            }), DispatcherPriority.Background);
        }

        public void ConnectSubjectBuffer(ITextBuffer subjectBuffer) { }
        public void DisconnectSubjectBuffer(ITextBuffer subjectBuffer) { }

        public void Detach(ITextView textView)
        {
            if (_view != textView) return;
            _view.TextBuffer.PostChanged -= OnPostChanged;
            _view = null;
        }
    }
}
