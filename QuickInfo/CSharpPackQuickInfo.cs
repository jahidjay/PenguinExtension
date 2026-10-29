using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Completion;
using PenguinExtention.Services;

namespace PenguinExtention.QuickInfo
{
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("PenguinExtension.PackCSharpQuickInfo")]
    [ContentType("CSharp")]
    [Order]
    internal sealed class CSharpPackQuickInfoProvider : IAsyncQuickInfoSourceProvider
    {
        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(typeof(CSharpPackQuickInfo), () => new CSharpPackQuickInfo(textBuffer));
    }

    /// <summary>
    /// Hover on an engine message declaration (Unity: <c>void Update()</c>, <c>void OnTriggerEnter(Collider other)</c>) shows
    /// what the engine does with it and a link to the docs for the project's editor version.
    /// </summary>
    internal sealed class CSharpPackQuickInfo : IAsyncQuickInfoSource
    {
        internal const string Probe = "C# pack hover (Unity message docs)";
        private readonly ITextBuffer _buffer;
        public CSharpPackQuickInfo(ITextBuffer buffer) { _buffer = buffer; }

        public Task<QuickInfoItem> GetQuickInfoItemAsync(IAsyncQuickInfoSession session, CancellationToken cancellationToken)
        {
            try
            {
                if (PackService.Callbacks.Count == 0) return Task.FromResult<QuickInfoItem>(null);
                var snapshot = _buffer.CurrentSnapshot;
                var point = session.GetTriggerPoint(snapshot);
                if (!point.HasValue) return Task.FromResult<QuickInfoItem>(null);
                int start = point.Value.Position, end = start;
                while (start > 0 && IsWord(snapshot[start - 1])) start--;
                while (end < snapshot.Length && IsWord(snapshot[end])) end++;
                if (end <= start) return Task.FromResult<QuickInfoItem>(null);
                var word = snapshot.GetText(start, end - start);
                var candidates = PackService.Callbacks.Where(c => c.Name == word && c.Language == "csharp").ToList();
                if (candidates.Count == 0) return Task.FromResult<QuickInfoItem>(null);
                var line = point.Value.GetContainingLine().GetText();
                if (!Regex.IsMatch(line, @"\b(?:void|IEnumerator)\s+" + Regex.Escape(word) + @"\s*\(")) return Task.FromResult<QuickInfoItem>(null);

                int from = Math.Max(0, start - 200000);
                var enclosing = CSharpPackContext.FindEnclosing(snapshot.GetText(from, start - from), start - from);
                var cb = CSharpPackContext.CallbacksFor(enclosing?.BaseType, "csharp", candidates).FirstOrDefault() ?? candidates[0];
                FeatureProbe.Mark(Probe, cb.Owner + "." + cb.Name);
                var span = snapshot.CreateTrackingSpan(start, end - start, SpanTrackingMode.EdgeInclusive);
                return Task.FromResult(new QuickInfoItem(span, Describe(cb, withLink: true)));
            }
            catch (Exception ex)
            {
                PenguinLog.Error("C# hover", ex);
                return Task.FromResult<QuickInfoItem>(null);
            }
        }

        /// <summary>Signature, description and docs link of an engine message (also the completion tooltip).</summary>
        internal static ContainerElement Describe(PackCallback cb, bool withLink)
        {
            var url = PackService.DocsUrl(cb);
            var rows = new System.Collections.Generic.List<object>
            {
                new ClassifiedTextElement(new ClassifiedTextRun("keyword", "🐧 " + cb.Owner + " message "), new ClassifiedTextRun("identifier", cb.Name)),
                new ClassifiedTextElement(new ClassifiedTextRun("text", cb.Signature)),
                new ClassifiedTextElement(new ClassifiedTextRun("text", cb.Description)),
            };
            if (url != null)
                rows.Add(withLink
                    ? new ClassifiedTextElement(new ClassifiedTextRun("text", "Docs: "),
                        new ClassifiedTextRun("text", url, () => Open(url), "Open " + (url.StartsWith("http") ? "the online docs" : "the installed docs")))
                    : new ClassifiedTextElement(new ClassifiedTextRun("text", "Docs: " + url)));
            return new ContainerElement(ContainerElementStyle.Stacked, rows);
        }

        private static void Open(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { PenguinLog.Error("C# hover", "open " + url + ": " + ex.Message); }
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

        public void Dispose() { }
    }
}
