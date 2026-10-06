using System;
using System.Collections.Generic;
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
using PenguinExtention.Core;
using Newtonsoft.Json.Linq;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Provides Unreal Engine symbol completions from the in-memory cache,
    /// AND context-aware macro specifier completions when the caret is inside
    /// UPROPERTY(), UFUNCTION(), UCLASS(), USTRUCT(), or UENUM() parentheses.
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

        private static readonly ImageElement SpecifierIcon =
            new ImageElement(new ImageId(ImageCatalogGuid, KnownImageIds.EnumerationItemPublic), "Specifier");

        // ── IAsyncCompletionSource ──────────────────────────────────

        public CompletionStartData InitializeCompletion(
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            CancellationToken token)
        {
            if (!BackendService.CompletionEnabled) return CompletionStartData.DoesNotParticipateInCompletion;
            var snapshot = triggerLocation.Snapshot;
            if (BackendService.IsCore)
            {
                if (!BackendService.IsReady) return CompletionStartData.DoesNotParticipateInCompletion;
                int start = triggerLocation.Position;
                while (start > 0 && IsIdentifierChar(snapshot[start - 1])) start--;
                return new CompletionStartData(CompletionParticipation.ProvidesItems,
                    new SnapshotSpan(snapshot, Span.FromBounds(start, triggerLocation.Position)));
            }
            var line = triggerLocation.GetContainingLine();
            var lineText = line.GetText();
            var column = triggerLocation.Position - line.Start.Position;

            // ── Check macro specifier context first ─────────────────
            var macroContext = DetectMacroContext(snapshot, triggerLocation);
            if (macroContext != null)
            {
                // ponytail: find the start of the current specifier token being typed
                int specStart = column - 1;
                while (specStart >= 0 && IsSpecifierChar(lineText[specStart]))
                    specStart--;
                specStart++;

                var applicableSpan = new SnapshotSpan(
                    snapshot,
                    new Span(line.Start.Position + specStart, column - specStart));

                return new CompletionStartData(
                    CompletionParticipation.ProvidesItems,
                    applicableSpan);
            }

            // ── Normal symbol completion ────────────────────────────
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
                return CompletionStartData.DoesNotParticipateInCompletion;

            // Walk backward to find the start of the identifier
            int identStart = column - 1;
            while (identStart >= 0 && IsIdentifierChar(lineText[identStart]))
                identStart--;
            identStart++;

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

            var span = new SnapshotSpan(
                snapshot,
                new Span(line.Start.Position + identStart, column - identStart));

            return new CompletionStartData(
                CompletionParticipation.ProvidesItems,
                span);
        }

        public async Task<CompletionContext> GetCompletionContextAsync(
            IAsyncCompletionSession session,
            CompletionTrigger trigger,
            SnapshotPoint triggerLocation,
            SnapshotSpan applicableToSpan,
            CancellationToken token)
        {
            if (!BackendService.CompletionEnabled) return CompletionContext.Empty;
            if (BackendService.IsCore)
            {
                try
                {
                    var result = await DocumentSync.RequestAsync("textDocument/completion", triggerLocation, token).ConfigureAwait(false);
                    var values = result as JArray ?? (result as JObject)?["items"] as JArray;
                    if (values == null) return CompletionContext.Empty;
                    var coreItems = ImmutableArray.CreateBuilder<CompletionItem>();
                    foreach (var value in values.OfType<JObject>().Take(200))
                    {
                        var label = (string)value["label"];
                        if (string.IsNullOrEmpty(label)) continue;
                        // Only edits representable by VS's applicable span are safe to commit.
                        // No snippets, additional edits, or custom/insert-replace ranges.
                        if ((int?)value["insertTextFormat"] == 2 || value["additionalTextEdits"] is JArray edits && edits.Count > 0) continue;
                        var textEdit = value["textEdit"] as JObject;
                        if (textEdit != null)
                        {
                            var range = textEdit["range"] as JObject;
                            var startLine = applicableToSpan.Start.GetContainingLine();
                            var endLine = applicableToSpan.End.GetContainingLine();
                            if ((int?)range?["start"]?["line"] != startLine.LineNumber ||
                                (int?)range?["start"]?["character"] != applicableToSpan.Start.Position - startLine.Start.Position ||
                                (int?)range?["end"]?["line"] != endLine.LineNumber ||
                                (int?)range?["end"]?["character"] != applicableToSpan.End.Position - endLine.Start.Position) continue;
                        }
                        var text = (string)textEdit?["newText"] ?? (string)value["insertText"] ?? label;
                        var item = new CompletionItem(label, this, SpecifierIcon, ImmutableArray<CompletionFilter>.Empty,
                            " (Core)", text, (string)value["sortText"] ?? label, (string)value["filterText"] ?? label, ImmutableArray<ImageElement>.Empty);
                        item.Properties["MacroSpecifierDesc"] = ((string)value["detail"] ?? "") + "\n" + (Protocol.PlainText(value["documentation"]) ?? "");
                        coreItems.Add(item);
                    }
                    return new CompletionContext(coreItems.ToImmutable());
                }
                catch (OperationCanceledException) { return CompletionContext.Empty; }
                catch (Exception ex) { BackendService.Report("Core completion: " + ex.Message); return CompletionContext.Empty; }
            }
            // ── Macro specifier mode ────────────────────────────────
            var macroContext = DetectMacroContext(triggerLocation.Snapshot, triggerLocation);
            if (macroContext != null)
            {
                return BuildSpecifierCompletions(macroContext.Value.macroName,
                                                  macroContext.Value.existingText,
                                                  session);
            }

            // ── Normal symbol mode ──────────────────────────────────
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
                return CompletionContext.Empty;

            var prefix = applicableToSpan.GetText();
            var symbols = await BackendService.SearchAsync(prefix, 100, token).ConfigureAwait(false);

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

        public async Task<object> GetDescriptionAsync(
            IAsyncCompletionSession session,
            CompletionItem item,
            CancellationToken token)
        {
            // ── Macro specifier description ─────────────────────────
            if (item.Properties.TryGetProperty("MacroSpecifierDesc", out string specDesc))
            {
                var container = new ContainerElement(
                    ContainerElementStyle.Stacked,
                    new ClassifiedTextElement(
                        new ClassifiedTextRun("keyword", "Specifier "),
                        new ClassifiedTextRun("identifier", item.DisplayText)),
                    new ClassifiedTextElement(
                        new ClassifiedTextRun("text", specDesc)));
                return container;
            }

            // ── Normal symbol description ───────────────────────────
            if (!item.Properties.TryGetProperty("UnrealSymbol", out UnrealSymbol sym))
                return null;

            var elements = new List<object>();

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
                var classInfo = await BackendService.ClassInfoAsync(sym, token).ConfigureAwait(false);
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

            var result = new ContainerElement(
                ContainerElementStyle.Stacked,
                elements.ToArray());

            return result;
        }

        // ── Macro context detection ─────────────────────────────────

        /// <summary>
        /// Scans backward from the caret to detect if we're inside a UE macro's parentheses.
        /// Returns the macro name and the existing text inside the parens (for filtering),
        /// or null if we're not in a macro context.
        /// ponytail: simple backward scan, no regex needed.
        /// </summary>
        private static (string macroName, string existingText)?
            DetectMacroContext(ITextSnapshot snapshot, SnapshotPoint point)
        {
            // ponytail: scan backward from caret looking for unmatched '('
            // then check if the word before '(' is a known macro name.
            // Limit scan to 500 chars to avoid scanning entire files.

            int pos = point.Position;
            int startPos = Math.Max(0, pos - 500);
            int parenDepth = 0;
            int openParenPos = -1;

            for (int i = pos - 1; i >= startPos; i--)
            {
                char c = snapshot[i];
                if (c == ')')
                    parenDepth++;
                else if (c == '(')
                {
                    if (parenDepth == 0)
                    {
                        openParenPos = i;
                        break;
                    }
                    parenDepth--;
                }
            }

            if (openParenPos < 0)
                return null;

            // Extract the word before '('
            int wordEnd = openParenPos;
            int wordStart = wordEnd - 1;
            while (wordStart >= startPos && snapshot[wordStart] == ' ')
                wordStart--; // skip whitespace between name and '('
            int nameEnd = wordStart + 1;
            while (wordStart >= startPos && char.IsLetterOrDigit(snapshot[wordStart]) || (wordStart >= startPos && snapshot[wordStart] == '_'))
                wordStart--;
            wordStart++;

            if (wordStart >= nameEnd)
                return null;

            var macroName = snapshot.GetText(wordStart, nameEnd - wordStart);

            if (!UnrealMacroSpecifiers.MacroNames.Contains(macroName))
                return null;

            // Get the text between '(' and caret for filtering already-used specifiers
            var existingText = snapshot.GetText(openParenPos + 1, pos - openParenPos - 1);

            return (macroName, existingText);
        }

        /// <summary>Build completion items for macro specifiers, filtering out already-used ones.</summary>
        private CompletionContext BuildSpecifierCompletions(string macroName, string existingText, IAsyncCompletionSession session)
        {
            var specifiers = UnrealMacroSpecifiers.GetSpecifiers(macroName);
            if (specifiers == null || specifiers.Length == 0)
                return CompletionContext.Empty;

            // ponytail: parse existing specifiers by splitting on commas
            var usedSpecifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in existingText.Split(','))
            {
                var trimmed = part.Trim();
                // Extract just the specifier name (before '=' or '(' if present)
                int eqIdx = trimmed.IndexOf('=');
                int parenIdx = trimmed.IndexOf('(');
                int cutoff = trimmed.Length;
                if (eqIdx >= 0) cutoff = Math.Min(cutoff, eqIdx);
                if (parenIdx >= 0) cutoff = Math.Min(cutoff, parenIdx);
                var name = trimmed.Substring(0, cutoff).Trim();
                if (name.Length > 0)
                    usedSpecifiers.Add(name);
            }

            var items = ImmutableArray.CreateBuilder<CompletionItem>();

            foreach (var spec in specifiers)
            {
                // Skip already-used specifiers (but allow 'meta' to be re-triggered)
                if (usedSpecifiers.Contains(spec.Name) && spec.Name != "meta")
                    continue;

                var insertText = spec.InsertText ?? spec.Name;
                var item = new CompletionItem(
                    displayText: spec.Name,
                    source: this,
                    icon: SpecifierIcon,
                    filters: ImmutableArray<CompletionFilter>.Empty,
                    suffix: $" ({macroName})",
                    insertText: insertText,
                    sortText: spec.Name,
                    filterText: spec.Name,
                    attributeIcons: ImmutableArray<ImageElement>.Empty);

                item.Properties["MacroSpecifierDesc"] = spec.Description;
                items.Add(item);
            }

            return items.Count > 0
                ? new CompletionContext(items.ToImmutable())
                : CompletionContext.Empty;
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static bool IsSpecifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }
    }
}
