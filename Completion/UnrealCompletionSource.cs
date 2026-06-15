using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Provides Unreal Engine symbol completions from the in-memory cache.
    /// Participates alongside VS's built-in C++ IntelliSense — our items are merged
    /// into the same completion list.
    /// </summary>
    internal sealed class UnrealCompletionSource : IAsyncCompletionSource
    {
        private static readonly Guid ImageCatalogGuid = new Guid("ae27a6b0-e345-4288-96df-5eaf394ee369");

        // Icon mapping: symbol kind → VS image moniker
        private static ImageElement GetIcon(UnrealSymbolKind kind)
        {
            int imageId;
            string automationName;

            switch (kind)
            {
                case UnrealSymbolKind.Class:
                case UnrealSymbolKind.Interface:
                    imageId = KnownImageIds.ClassPublic;
                    automationName = "Class";
                    break;
                case UnrealSymbolKind.Struct:
                    imageId = KnownImageIds.StructurePublic;
                    automationName = "Struct";
                    break;
                case UnrealSymbolKind.Enum:
                case UnrealSymbolKind.EnumValue:
                    imageId = KnownImageIds.EnumerationPublic;
                    automationName = "Enum";
                    break;
                case UnrealSymbolKind.Function:
                case UnrealSymbolKind.Delegate:
                    imageId = KnownImageIds.MethodPublic;
                    automationName = "Function";
                    break;
                case UnrealSymbolKind.Property:
                    imageId = KnownImageIds.PropertyPublic;
                    automationName = "Property";
                    break;
                case UnrealSymbolKind.Macro:
                    imageId = KnownImageIds.MacroPublic;
                    automationName = "Macro";
                    break;
                default:
                    imageId = KnownImageIds.ClassPublic;
                    automationName = "Symbol";
                    break;
            }

            return new ImageElement(new ImageId(ImageCatalogGuid, imageId), automationName);
        }

        // ── IAsyncCompletionSource ──────────────────────────────────

        public CompletionStartData InitializeCompletion(
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            CancellationToken token)
        {
            // Don't participate if cache isn't loaded or the feature is disabled
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
                return CompletionStartData.DoesNotParticipateInCompletion;

            // Determine the "applicable to" span — the identifier being typed
            var snapshot = triggerLocation.Snapshot;
            var line = triggerLocation.GetContainingLine();
            var lineText = line.GetText();
            var column = triggerLocation.Position - line.Start.Position;

            // Walk backward to find the start of the identifier
            int identStart = column - 1;
            while (identStart >= 0 && IsIdentifierChar(lineText[identStart]))
                identStart--;
            identStart++; // move past the non-identifier char

            // Check for trigger contexts: ::, ->, ., or plain identifier
            bool shouldParticipate = false;

            if (trigger.Reason == CompletionTriggerReason.Insertion)
            {
                char typedChar = trigger.Character;
                if (char.IsLetterOrDigit(typedChar) || typedChar == '_')
                {
                    shouldParticipate = true;
                }
                else if (typedChar == ':' && column >= 2 && lineText[column - 2] == ':')
                {
                    shouldParticipate = true;
                    identStart = column; // after ::
                }
                else if (typedChar == '>' && column >= 2 && lineText[column - 2] == '-')
                {
                    shouldParticipate = true;
                    identStart = column; // after ->
                }
                else if (typedChar == '.')
                {
                    shouldParticipate = true;
                    identStart = column; // after .
                }
            }
            else if (trigger.Reason == CompletionTriggerReason.Invoke ||
                     trigger.Reason == CompletionTriggerReason.InvokeAndCommitIfUnique)
            {
                shouldParticipate = true;
            }

            if (!shouldParticipate)
                return CompletionStartData.DoesNotParticipateInCompletion;

            var applicableSpan = new SnapshotSpan(
                snapshot,
                new Span(line.Start.Position + identStart, column - identStart));

            return new CompletionStartData(
                CompletionParticipation.ProvidesItems,
                applicableSpan);
        }

        public async Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
                return CompletionContext.Empty;

            // Get the text the user has typed so far
            var prefix = applicableToSpan.GetText();

            // Search the cache
            var symbols = cache.Search(prefix, kindFilter: null, limit: 100);

            if (symbols.Count == 0)
                return CompletionContext.Empty;

            var items = ImmutableArray.CreateBuilder<CompletionItem>(symbols.Count);

            foreach (var sym in symbols)
            {
                var icon = GetIcon(sym.Kind);
                var suffix = sym.IsEngineSymbol ? " (Engine)" : "";
                var sortText = $"{(sym.IsEngineSymbol ? "1" : "0")}_{sym.Name}";

                var item = new CompletionItem(
                    displayText: sym.Name,
                    source: this,
                    icon: icon,
                    filters: ImmutableArray<CompletionFilter>.Empty,
                    suffix: suffix,
                    insertText: sym.Name,
                    sortText: sortText,
                    filterText: sym.Name,
                    attributeIcons: ImmutableArray<ImageElement>.Empty);

                // Stash the symbol in item Properties for use in GetDescriptionAsync
                item.Properties["UnrealSymbol"] = sym;

                items.Add(item);
            }

            return new CompletionContext(items.ToImmutable());
        }

        public Task<object> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
        {
            if (!item.Properties.TryGetProperty("UnrealSymbol", out UnrealSymbol sym))
                return Task.FromResult<object>(null);

            // Build rich description
            var elements = new System.Collections.Generic.List<object>();

            // Signature
            if (!string.IsNullOrEmpty(sym.Signature))
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("keyword", sym.KindDisplay + " "),
                    new ClassifiedTextRun("text", sym.Signature)));
            }

            // File location
            var fileName = System.IO.Path.GetFileName(sym.FilePath);
            elements.Add(new ClassifiedTextElement(
                new ClassifiedTextRun("text", $"📁 {fileName}:{sym.LineNumber}")));

            // Owner class
            if (!string.IsNullOrEmpty(sym.OwnerClass))
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("text", $"📦 Member of {sym.OwnerClass}")));
            }

            // Inheritance chain for classes
            if (sym.Kind == UnrealSymbolKind.Class || sym.Kind == UnrealSymbolKind.Struct)
            {
                var classInfo = CacheService.Instance?.GetClassInfo(sym.Name);
                if (classInfo?.InheritanceChain?.Count > 0)
                {
                    var chain = string.Join(" → ", classInfo.InheritanceChain);
                    elements.Add(new ClassifiedTextElement(
                        new ClassifiedTextRun("text", $"🔗 {sym.Name} → {chain}")));
                }
            }

            // Comment
            if (!string.IsNullOrEmpty(sym.Comment))
            {
                elements.Add(new ClassifiedTextElement(
                    new ClassifiedTextRun("text", $"📝 {sym.Comment}")));
            }

            // Source (engine vs project)
            var source = sym.IsEngineSymbol ? "Engine" : "Project";
            elements.Add(new ClassifiedTextElement(
                new ClassifiedTextRun("text", $"[{source}]")));

            var container = new ContainerElement(
                ContainerElementStyle.Stacked,
                elements.ToArray());

            return Task.FromResult<object>(container);
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
