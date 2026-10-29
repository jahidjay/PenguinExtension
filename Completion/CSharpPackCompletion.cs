using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.QuickInfo;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>Adds enabled C# packs' items (Unity message stubs, snippets) to Visual Studio's C# completion list.</summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("PenguinExtension.PackCSharpCompletionSource")]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CSharpPackCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(CSharpPackCompletionSource), () => new CSharpPackCompletionSource());
    }

    internal sealed class CSharpPackCompletionSource : IAsyncCompletionSource
    {
        internal const string Probe = "C# pack completion (Unity messages/snippets)";
        internal const string TextKey = "PenguinExtension.PackText";
        internal const string ClassKey = "PenguinExtension.PackClass";
        internal const string CaretBackKey = "PenguinExtension.PackCaretBack";
        private const string Language = "csharp";
        /// <summary>Tools → Options → PenguinExtension → Unity Message Completion.</summary>
        internal static volatile bool OfferCallbacks = true;
        private static readonly Guid Catalog = KnownImageIds.ImageCatalogGuid;
        private static readonly ImageElement MethodIcon = new ImageElement(new ImageId(Catalog, KnownImageIds.MethodProtected), "Unity message");
        private static readonly ImageElement SnippetIcon = new ImageElement(new ImageId(Catalog, KnownImageIds.Snippet), "Snippet");

        public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
        {
            try
            {
                if (!PackService.HasLanguage(Language)) return CompletionStartData.DoesNotParticipateInCompletion;
                if (trigger.Reason == CompletionTriggerReason.Insertion && !(char.IsLetter(trigger.Character) || trigger.Character == '_'))
                    return CompletionStartData.DoesNotParticipateInCompletion;
                var snapshot = triggerLocation.Snapshot;
                int start = triggerLocation.Position;
                while (start > 0 && (char.IsLetterOrDigit(snapshot[start - 1]) || snapshot[start - 1] == '_')) start--;
                var line = triggerLocation.GetContainingLine();
                var prefix = snapshot.GetText(line.Start, start - line.Start);
                if (!CSharpPackContext.IsLeadIn(prefix)) return CompletionStartData.DoesNotParticipateInCompletion;
                return new CompletionStartData(CompletionParticipation.ProvidesItems, new SnapshotSpan(snapshot, Span.FromBounds(start, triggerLocation.Position)));
            }
            catch (Exception ex)
            {
                PenguinLog.Error("C# completion", ex);
                return CompletionStartData.DoesNotParticipateInCompletion;
            }
        }

        public Task<CompletionContext> GetCompletionContextAsync(IAsyncCompletionSession session, CompletionTrigger trigger,
            SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
        {
            try
            {
                var snapshot = triggerLocation.Snapshot;
                int caret = applicableToSpan.Start.Position;
                int from = Math.Max(0, caret - 200000);
                var before = snapshot.GetText(from, caret - from);
                var enclosing = CSharpPackContext.FindEnclosing(before, before.Length);
                var items = ImmutableArray.CreateBuilder<CompletionItem>();

                if (OfferCallbacks && enclosing?.Depth == 1)
                {
                    var declared = CSharpPackContext.DeclaredMethods(snapshot.GetText());
                    foreach (var cb in CSharpPackContext.CallbacksFor(enclosing.Value.BaseType, Language, PackService.Callbacks))
                    {
                        if (declared.Contains(cb.Name)) continue;
                        var item = new CompletionItem(cb.Name, this, MethodIcon, ImmutableArray<CompletionFilter>.Empty, cb.Owner + " message",
                            cb.Name, "~" + cb.Name, cb.Name, ImmutableArray<ImageElement>.Empty);
                        item.Properties.AddProperty(TextKey, cb.Stub);
                        item.Properties.AddProperty(typeof(PackCallback), cb);
                        item.Properties.AddProperty(ClassKey, enclosing.Value.ClassName);
                        items.Add(item);
                    }
                }
                foreach (var s in PackService.SnippetsFor(Language))
                {
                    var item = new CompletionItem(s.Keyword, this, SnippetIcon, ImmutableArray<CompletionFilter>.Empty, "🐧 snippet",
                        s.Keyword, "~~" + s.Keyword, s.Keyword, ImmutableArray<ImageElement>.Empty);
                    item.Properties.AddProperty(TextKey, s.Text);
                    item.Properties.AddProperty(typeof(UnrealSnippets.Snippet), s);
                    item.Properties.AddProperty(ClassKey, enclosing?.ClassName);
                    item.Properties.AddProperty(CaretBackKey, s.CaretBack);
                    items.Add(item);
                }
                FeatureProbe.Mark(Probe, items.Count + " items" + (enclosing == null ? "" : " in " + enclosing.Value.ClassName + " : " + (enclosing.Value.BaseType ?? "?")));
                return Task.FromResult(new CompletionContext(items.ToImmutable()));
            }
            catch (Exception ex)
            {
                PenguinLog.Error("C# completion", ex);
                return Task.FromResult(CompletionContext.Empty);
            }
        }

        public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token)
        {
            if (item.Properties.TryGetProperty(typeof(PackCallback), out PackCallback cb))
                return Task.FromResult<object>(CSharpPackQuickInfo.Describe(cb, withLink: false));
            if (item.Properties.TryGetProperty(typeof(UnrealSnippets.Snippet), out UnrealSnippets.Snippet s))
                return Task.FromResult<object>(new ContainerElement(ContainerElementStyle.Stacked,
                    new ClassifiedTextElement(new ClassifiedTextRun("keyword", "Snippet "), new ClassifiedTextRun("identifier", s.Keyword),
                        new ClassifiedTextRun("text", "  (pack " + s.PackId + ")")),
                    new ClassifiedTextElement(new ClassifiedTextRun("text", s.Description)),
                    new ClassifiedTextElement(new ClassifiedTextRun("text", s.Text.Replace("$0", "").Replace("$CLASS", "<class>")))));
            return Task.FromResult<object>(null);
        }
    }

    /// <summary>Inserts pack items with indentation and caret placement (default commit would paste the raw text).</summary>
    [Export(typeof(IAsyncCompletionCommitManagerProvider))]
    [Name("PenguinExtension.PackCSharpCommitManager")]
    [ContentType("CSharp")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class CSharpPackCommitManagerProvider : IAsyncCompletionCommitManagerProvider
    {
        public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
            textView.Properties.GetOrCreateSingletonProperty(typeof(CSharpPackCommitManager), () => new CSharpPackCommitManager(textView));
    }

    internal sealed class CSharpPackCommitManager : IAsyncCompletionCommitManager
    {
        private readonly ITextView _view;
        public CSharpPackCommitManager(ITextView view) { _view = view; }

        public IEnumerable<char> PotentialCommitCharacters => Array.Empty<char>();

        public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => false;

        public CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, CompletionItem item, char typedChar, CancellationToken token)
        {
            if (!item.Properties.TryGetProperty(CSharpPackCompletionSource.TextKey, out string template)) return CommitResult.Unhandled;
            if (typedChar != '\0' && typedChar != '\t' && typedChar != '\n' && typedChar != '\r') return CommitResult.Unhandled;
            try
            {
                item.Properties.TryGetProperty(CSharpPackCompletionSource.ClassKey, out string className);
                item.Properties.TryGetProperty(CSharpPackCompletionSource.CaretBackKey, out int caretBack);
                var snapshot = buffer.CurrentSnapshot;
                var span = session.ApplicableToSpan.GetSpan(snapshot);
                var line = span.Start.GetContainingLine();
                var lineText = line.GetText();
                int indentLength = 0;
                while (indentLength < lineText.Length && char.IsWhiteSpace(lineText[indentLength])) indentLength++;
                var indent = lineText.Substring(0, indentLength);
                // "private void Upd|" or "[Ser|": replace the typed lead-in too, the template carries its own.
                int start = span.Start.Position;
                var prefix = snapshot.GetText(line.Start, start - line.Start);
                if (prefix.Trim().Length > 0 && CSharpPackContext.IsLeadIn(prefix)) start = line.Start + indentLength;
                var newline = line.LineBreakLength > 0 ? line.GetLineBreakText() : (snapshot.LineCount > 1 ? snapshot.GetLineFromLineNumber(0).GetLineBreakText() : Environment.NewLine);
                if (string.IsNullOrEmpty(newline)) newline = Environment.NewLine;
                bool useTabs = !_view.Options.GetOptionValue(DefaultOptions.ConvertTabsToSpacesOptionId);
                var text = CSharpPackContext.Expand(template, className, indent, newline, useTabs, caretBack, out int caret);
                using (var edit = buffer.CreateEdit())
                {
                    edit.Replace(Span.FromBounds(start, span.End.Position), text);
                    snapshot = edit.Apply();
                }
                _view.Caret.MoveTo(new SnapshotPoint(snapshot, Math.Min(snapshot.Length, start + caret)));
                return CommitResult.Handled;
            }
            catch (Exception ex)
            {
                PenguinLog.Error("C# completion", ex);
                return CommitResult.Unhandled;
            }
        }
    }
}
