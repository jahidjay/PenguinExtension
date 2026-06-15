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
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
                return null;

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

            // Look up in cache
            var symbols = cache.GetByExactName(word);
            if (symbols.Count == 0)
                return null;

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
                    var classInfo = cache.GetClassInfo(sym.Name);
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
                cache.RecordUsage(sym.Id);
            }

            var applicableSpan = _textBuffer.CurrentSnapshot.CreateTrackingSpan(
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
