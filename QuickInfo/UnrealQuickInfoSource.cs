using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Operations;
using PenguinExtention.Models;
using PenguinExtention.Core;
using PenguinExtention.Services;

namespace PenguinExtention.QuickInfo
{
    /// <summary>
    /// Provides Unreal Engine metadata tooltips on hover.
    /// Shows declaration signature, meta specifiers, inheritance chain, file location, and doc comment.
    /// </summary>
    internal sealed class UnrealQuickInfoSource : IAsyncQuickInfoSource
    {
        private readonly ITextBuffer _textBuffer;
        private readonly ITextStructureNavigatorSelectorService _navigatorService;
        private bool _disposed;

        public UnrealQuickInfoSource(
            ITextBuffer textBuffer,
            ITextStructureNavigatorSelectorService navigatorService)
        {
            _textBuffer = textBuffer;
            _navigatorService = navigatorService;
        }

        public async Task<QuickInfoItem> GetQuickInfoItemAsync(
            IAsyncQuickInfoSession session,
            CancellationToken cancellationToken)
        {
            if (!BackendService.HoverEnabled || !BackendService.IsReady) return null;

            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            // Get the word under the cursor
            var triggerPoint = session.GetTriggerPoint(_textBuffer.CurrentSnapshot);
            if (!triggerPoint.HasValue)
                return null;

            var navigator = _navigatorService.GetTextStructureNavigator(_textBuffer);
            var extent = navigator.GetExtentOfWord(triggerPoint.Value);

            if (!extent.IsSignificant)
                return null;

            var word = extent.Span.GetText();
            if (string.IsNullOrWhiteSpace(word) || word.Length < 2)
                return null;

            if (BackendService.IsCore)
            {
                try
                {
                    var response = await DocumentSync.RequestAsync("textDocument/hover", triggerPoint.Value, cancellationToken).ConfigureAwait(false);
                    var text = Protocol.PlainText((response as Newtonsoft.Json.Linq.JObject)?["contents"]);
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    return new QuickInfoItem(extent.Span.Snapshot.CreateTrackingSpan(extent.Span, SpanTrackingMode.EdgeInclusive),
                        new ClassifiedTextElement(new ClassifiedTextRun("text", text)));
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex) { BackendService.Report("Core hover: " + ex.Message); return null; }
            }
            var symbols = await BackendService.ExactAsync(word, cancellationToken).ConfigureAwait(false);
            if (symbols.Count == 0) return null;

            // Build tooltip content
            var elements = new List<object>();

            foreach (var sym in symbols.Take(3)) // Limit to 3 overloads in tooltip
            {
                // Header: Kind + signature
                var headerRuns = new List<ClassifiedTextRun>();
                headerRuns.Add(new ClassifiedTextRun("keyword", $"[{sym.KindDisplay}] "));

                if (!string.IsNullOrEmpty(sym.Signature))
                {
                    headerRuns.Add(new ClassifiedTextRun("text", sym.Signature));
                }
                else
                {
                    headerRuns.Add(new ClassifiedTextRun("identifier", sym.Name));
                }

                elements.Add(new ClassifiedTextElement(headerRuns.ToArray()));

                // Meta specifiers for classes/structs
                if (sym.Kind == UnrealSymbolKind.Class || sym.Kind == UnrealSymbolKind.Struct ||
                    sym.Kind == UnrealSymbolKind.Interface)
                {
                    var classInfo = await BackendService.ClassInfoAsync(sym, cancellationToken).ConfigureAwait(false);
                    if (classInfo != null)
                    {
                        // Meta specifiers
                        if (!string.IsNullOrEmpty(classInfo.MetaSpecifiers))
                        {
                            elements.Add(new ClassifiedTextElement(
                                new ClassifiedTextRun("text", $"⚙ {classInfo.MetaSpecifiers}")));
                        }

                        // Inheritance chain
                        if (classInfo.InheritanceChain?.Count > 0)
                        {
                            var chain = sym.Name + " → " + string.Join(" → ", classInfo.InheritanceChain);
                            elements.Add(new ClassifiedTextElement(
                                new ClassifiedTextRun("text", $"🔗 {chain}")));
                        }
                        else if (!string.IsNullOrEmpty(classInfo.BaseClass))
                        {
                            elements.Add(new ClassifiedTextElement(
                                new ClassifiedTextRun("text", $"🔗 {sym.Name} → {classInfo.BaseClass}")));
                        }
                    }
                }

                // Owner class for members
                if (!string.IsNullOrEmpty(sym.OwnerClass))
                {
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun("text", $"📦 Member of {sym.OwnerClass}")));
                }

                // File location
                var fileName = Path.GetFileName(sym.FilePath);
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("text", $"📁 {fileName}:{sym.LineNumber}")));

                // Doc comment
                if (!string.IsNullOrEmpty(sym.Comment))
                {
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun("text", $"📝 {sym.Comment}")));
                }

                // Source indicator
                var source = sym.IsEngineSymbol ? "Engine" : "Project";
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("text", $"[{source}]")));

                // Separator between overloads
                if (symbols.Count > 1)
                {
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun("text", "────────────────────────────")));
                }
            }

            if (symbols.Count > 3)
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("text", $"... and {symbols.Count - 3} more")));
            }

            var container = new ContainerElement(
                ContainerElementStyle.Stacked,
                elements.ToArray());

            // Record usage for hot-symbol tracking
            foreach (var sym in symbols)
            {
                BackendService.RecordUsage(sym);
            }

            var applicableSpan = extent.Span.Snapshot.CreateTrackingSpan(
                extent.Span,
                SpanTrackingMode.EdgeInclusive);

            return new QuickInfoItem(applicableSpan, container);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
