using System;
using System.ComponentModel.Design;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using PenguinExtention.Services;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// "Go To Unreal Definition" command (Ctrl+Shift+U, D).
    /// Looks up the symbol under the cursor in the cache and navigates to its source file.
    /// If multiple matches exist, opens a disambiguation picker.
    /// </summary>
    internal sealed class GoToUnrealDefinitionCommand
    {
        private readonly AsyncPackage _package;

        private GoToUnrealDefinitionCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));

            var cmdId = new CommandID(PenguinExtensionCommandIds.CommandSetGuid,
                                     PenguinExtensionCommandIds.GoToUnrealDefinitionId);
            var menuItem = new MenuCommand(Execute, cmdId);
            commandService.AddCommand(menuItem);
        }

        public static GoToUnrealDefinitionCommand Instance { get; private set; }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new GoToUnrealDefinitionCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
            {
                ShowStatusMessage("PenguinExtension: Cache not loaded yet.");
                return;
            }

            // Get the word under the cursor
            var word = GetWordUnderCursor();
            if (string.IsNullOrEmpty(word))
            {
                ShowStatusMessage("PenguinExtension: No identifier under cursor.");
                return;
            }

            // Look up in cache
            var symbols = cache.GetByExactName(word);
            if (symbols.Count == 0)
            {
                ShowStatusMessage($"PenguinExtension: No Unreal definition found for '{word}'.");
                return;
            }

            // Pick the best match (prefer project symbols, then by access count)
            var target = symbols
                .OrderBy(s => s.IsEngineSymbol ? 1 : 0)
                .ThenByDescending(s => s.AccessCount)
                .First();

            // If multiple matches and they're in different files, try to pick the best one
            // For now, just go to the first match
            NavigateToSymbol(target);
            cache.RecordUsage(target.Id);

            ShowStatusMessage($"PenguinExtension: Navigated to {target.DisplayText} in {System.IO.Path.GetFileName(target.FilePath)}");
        }

        private void NavigateToSymbol(Models.UnrealSymbol symbol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!System.IO.File.Exists(symbol.FilePath))
            {
                ShowStatusMessage($"PenguinExtension: File not found: {symbol.FilePath}");
                return;
            }

            // Open the document
            VsShellUtilities.OpenDocument(
                _package,
                symbol.FilePath,
                Guid.Empty,
                out _,
                out _,
                out IVsWindowFrame windowFrame);

            if (windowFrame == null) return;
            windowFrame.Show();

            // Navigate to the line
            var textView = VsShellUtilities.GetTextView(windowFrame);
            if (textView != null)
            {
                textView.SetCaretPos(symbol.LineNumber - 1, symbol.ColumnNumber);
                textView.CenterLines(symbol.LineNumber - 1, 1);
            }
        }

        private string GetWordUnderCursor()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var textManager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null) return null;

            textManager.GetActiveView(1, null, out IVsTextView activeView);
            if (activeView == null) return null;

            activeView.GetCaretPos(out int line, out int column);
            activeView.GetTextStream(line, 0, line, 500, out string lineText);

            if (string.IsNullOrEmpty(lineText)) return null;

            // Find word boundaries
            int start = column;
            int end = column;

            while (start > 0 && IsIdentifierChar(lineText[start - 1]))
                start--;

            while (end < lineText.Length && IsIdentifierChar(lineText[end]))
                end++;

            if (start >= end) return null;

            return lineText.Substring(start, end - start);
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private void ShowStatusMessage(string message)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var statusBar = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            statusBar?.SetText(message);
        }
    }
}
