using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Commands
{
    /// <summary>A single text replacement, addressed by 1-based line and 0-based column.</summary>
    internal sealed class TextChange
    {
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public int ColumnNumber { get; set; }
        public int Length { get; set; }
        public string NewText { get; set; }
        /// <summary>Text expected at the location; the change is skipped when the file no longer matches.</summary>
        public string ExpectedText { get; set; }
    }

    /// <summary>Shared editor plumbing for Penguin commands. All members require the UI thread unless noted.</summary>
    internal static class PenguinEditor
    {
        /// <summary>Status bar + Output pane log line (the status bar alone is overwritten too quickly to read).</summary>
        public static void Status(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            PenguinLog.Info("Command", message);
            (Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText("🐧 " + message);
        }

        /// <summary>A command could not do its job: status bar, error line in the Output pane, and the pane is shown.</summary>
        public static void Fail(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            PenguinLog.Error("Command", message);
            PenguinLog.Show();
            (Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText("🐧 " + message);
        }

        public static string FilePathOf(ITextView view)
        {
            if (view == null) return null;
            return view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) ? document.FilePath : null;
        }

        public static string FilePathOf(ITextBuffer buffer) =>
            buffer != null && buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document) ? document.FilePath : null;

        /// <summary>Opens <paramref name="path"/> and places the caret at a 1-based line / 0-based column.</summary>
        public static ITextView Open(string path, int line = 0, int column = 0)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Status("File not found: " + path); return null; }
            VsShellUtilities.OpenDocument(PenguinExtensionPackage.Instance, path, Guid.Empty, out _, out _, out IVsWindowFrame frame);
            if (frame == null) return null;
            frame.Show();
            var vsView = VsShellUtilities.GetTextView(frame);
            if (vsView == null) return null;
            if (line > 0)
            {
                vsView.SetCaretPos(line - 1, Math.Max(0, column));
                vsView.CenterLines(line - 1, 1);
            }
            return EditorAccess.Adapt(vsView);
        }

        public static void Navigate(UnrealSymbol symbol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (symbol == null) return;
            Open(symbol.FilePath, symbol.LineNumber, symbol.ColumnNumber);
            BackendService.RecordUsage(symbol);
        }

        public static string WordAtCaret(ITextView view, out SnapshotSpan span)
        {
            span = default(SnapshotSpan);
            if (view == null) return null;
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            var word = OccurrenceScanner.IdentifierAt(line.GetText(), caret.Position - line.Start.Position, out int start);
            if (word != null) span = new SnapshotSpan(line.Start + start, word.Length);
            return word;
        }

        /// <summary>Text immediately before <paramref name="span"/> on its line, used for owner context (<c>Owner::</c>).</summary>
        public static string QualifierBefore(SnapshotSpan span)
        {
            if (span.Snapshot == null) return null;
            var line = span.Start.GetContainingLine();
            var prefix = line.Snapshot.GetText(line.Start, span.Start - line.Start).TrimEnd();
            if (!prefix.EndsWith("::")) return null;
            prefix = prefix.Substring(0, prefix.Length - 2).TrimEnd();
            int s = prefix.Length;
            while (s > 0 && OccurrenceScanner.IsIdentPart(prefix[s - 1])) s--;
            return s < prefix.Length ? prefix.Substring(s) : null;
        }

        /// <summary>
        /// Best indexed symbol for the identifier under the caret. Prefers an explicit <c>Owner::</c> qualifier,
        /// then symbols declared in the current file or its header/source partner, then project over engine.
        /// Must be called on the UI thread; completes on a background thread.
        /// </summary>
        public static async Task<UnrealSymbol> SymbolAtCaretAsync(ITextView view, CancellationToken token)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
            var word = WordAtCaret(view, out var span);
            if (string.IsNullOrEmpty(word)) return null;
            var qualifier = QualifierBefore(span);
            var file = FilePathOf(view);
            await TaskScheduler.Default;
            var candidates = await BackendService.ExactAsync(word, token).ConfigureAwait(false);
            return Best(candidates, qualifier, file);
        }

        public static UnrealSymbol Best(IEnumerable<UnrealSymbol> candidates, string qualifier, string currentFile)
        {
            var partner = currentFile == null ? null : HeaderSourceResolver.Partner(currentFile);
            return candidates
                .OrderBy(s => qualifier != null && s.OwnerClass == qualifier ? 0 : 1)
                .ThenBy(s => SameFile(s.FilePath, currentFile) || SameFile(s.FilePath, partner) ? 0 : 1)
                .ThenBy(s => s.IsEngineSymbol ? 1 : 0)
                .ThenByDescending(s => s.AccessCount)
                .FirstOrDefault();
        }

        public static bool SameFile(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Applies changes across files inside one linked undo transaction, opening files that aren't open.
        /// Returns the number of changes applied (stale changes whose expected text no longer matches are skipped).
        /// </summary>
        public static int ApplyChanges(IReadOnlyList<TextChange> changes, string description)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (changes == null || changes.Count == 0) return 0;

            var linked = Package.GetGlobalService(typeof(SVsLinkedUndoTransactionManager)) as IVsLinkedUndoTransactionManager;
            bool linkedOpen = linked != null && ErrorHandler.Succeeded(linked.OpenLinkedUndo((uint)LinkedTransactionFlags2.mdtGlobal, description));
            int applied = 0;
            try
            {
                foreach (var group in changes.GroupBy(c => Path.GetFullPath(c.FilePath), StringComparer.OrdinalIgnoreCase))
                {
                    var view = Open(group.Key);
                    if (view == null) continue;
                    var snapshot = view.TextBuffer.CurrentSnapshot;
                    using (var edit = view.TextBuffer.CreateEdit())
                    {
                        foreach (var c in group)
                        {
                            if (c.LineNumber < 1 || c.LineNumber > snapshot.LineCount) continue;
                            var line = snapshot.GetLineFromLineNumber(c.LineNumber - 1);
                            int start = line.Start.Position + c.ColumnNumber;
                            if (c.ColumnNumber < 0 || c.ColumnNumber > line.Length || start + c.Length > snapshot.Length) continue;
                            if (c.ExpectedText != null && snapshot.GetText(start, c.Length) != c.ExpectedText) continue;
                            if (edit.Replace(start, c.Length, c.NewText)) applied++;
                        }
                        edit.Apply();
                    }
                }
                if (linkedOpen)
                {
                    if (ErrorHandler.Failed(linked.CloseLinkedUndo()))
                        PenguinLog.Error("Edit", "could not merge \"" + description + "\" into one undo step; undo each file separately.");
                    linkedOpen = false;
                }
            }
            finally
            {
                if (linkedOpen) linked.AbortLinkedUndo();
            }
            return applied;
        }

        /// <summary>Inserts the planned #include line (its own undo step) and reports it.</summary>
        public static void InsertInclude(ITextView view, IncludeInserter.Plan plan)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = view.TextSnapshot;
            var newline = snapshot.LineCount > 1 ? snapshot.GetLineFromLineNumber(0).GetLineBreakText() : Environment.NewLine;
            if (string.IsNullOrEmpty(newline)) newline = Environment.NewLine;
            int target = Math.Min(plan.Line, snapshot.LineCount);
            var position = target >= snapshot.LineCount ? snapshot.Length : snapshot.GetLineFromLineNumber(target).Start.Position;
            var prefix = target >= snapshot.LineCount && snapshot.Length > 0 && !snapshot.GetText().EndsWith("\n") ? newline : "";
            EditorAccess.Insert(view, position, prefix + "#include \"" + plan.IncludePath + "\"" + newline, "Add include " + plan.IncludePath);
            Status("Added #include \"" + plan.IncludePath + "\"");
        }

        /// <summary>Runs an async command body, reporting failures on the status bar.</summary>
        public static void Run(Func<Task> body) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await body(); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!(ex is InvalidOperationException)) PenguinLog.Error("Command", ex); // unexpected: keep the stack trace
                Fail(ex.Message);
            }
        });
    }
}
