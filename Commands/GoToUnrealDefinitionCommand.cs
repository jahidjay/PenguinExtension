using System;
using System.ComponentModel.Design;
using System.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using PenguinExtention.Services;
using PenguinExtention.Core;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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

        private void Execute(object sender, EventArgs e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await ExecuteAsync(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); ShowStatusMessage(ex.Message); }
        });

        private async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!BackendService.IsReady) { ShowStatusMessage(BackendService.Status); return; }
            if (BackendService.IsCore)
            {
                var view = EditorAccess.ActiveView();
                if (view == null) return;
                var result = await DocumentSync.RequestAsync("textDocument/definition", view.Caret.Position.BufferPosition, _package.DisposalToken);
                var target = result is JArray array ? array.FirstOrDefault() : result;
                if (target == null || target.Type == JTokenType.Null) { ShowStatusMessage("Core: No definition found."); return; }
                var uri = (string)target?["uri"] ?? (string)target?["targetUri"];
                var start = target?["range"]?["start"] ?? target?["targetSelectionRange"]?["start"];
                if (uri == null) { ShowStatusMessage("Core: No definition found."); return; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                NavigateToSymbol(new Models.UnrealSymbol { FilePath = new Uri(uri).LocalPath,
                    LineNumber = ((int?)start?["line"] ?? 0) + 1, ColumnNumber = (int?)start?["character"] ?? 0 });
                return;
            }
            var word = GetWordUnderCursor();
            if (string.IsNullOrEmpty(word)) return;
            var symbols = await BackendService.ExactAsync(word, _package.DisposalToken);
            if (symbols.Count == 0)
            {
                await TryNavigateToEngineMacroAsync(word);
                return;
            }
            var symbol = symbols.OrderBy(s => s.IsEngineSymbol ? 1 : 0).ThenByDescending(s => s.AccessCount).First();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            NavigateToSymbol(symbol);
            BackendService.RecordUsage(symbol);
        }

        private void NavigateToSymbol(Models.UnrealSymbol symbol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

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

            var editor = EditorAccess.ActiveView();
            if (editor == null) return null;
            var caret = editor.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            var lineText = line.GetText();

            if (string.IsNullOrEmpty(lineText)) return null;

            // Find word boundaries
            int start = Math.Min(caret.Position - line.Start.Position, lineText.Length);
            int end = start;

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

        // ── Engine macro fallback ───────────────────────────────────

        // ponytail: known UE macros that live in ObjectMacros.h
        private static readonly System.Collections.Generic.HashSet<string> KnownEngineMacros =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                "UCLASS", "USTRUCT", "UENUM", "UFUNCTION", "UPROPERTY",
                "UINTERFACE", "UMETA", "UPARAM",
                "GENERATED_BODY", "GENERATED_UCLASS_BODY", "GENERATED_USTRUCT_BODY",
                "DECLARE_DYNAMIC_MULTICAST_DELEGATE", "DECLARE_DELEGATE",
            };

        /// <summary>
        /// If the word is a known UE macro keyword, navigate to ObjectMacros.h in the engine source.
        /// Returns true if navigation was attempted.
        /// </summary>
        private async Task<bool> TryNavigateToEngineMacroAsync(string word)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Check prefix match for DECLARE_* family
            bool isMacro = KnownEngineMacros.Contains(word)
                        || word.StartsWith("DECLARE_", StringComparison.Ordinal);

            if (!isMacro)
                return false;

            var detector = UnrealProjectDetector.Instance;
            if (detector == null || string.IsNullOrEmpty(detector.EngineSourceRoot))
            {
                ShowStatusMessage("PenguinExtension: Engine source path not found. Set it in Tools → Options → PenguinExtension.");
                return true; // we handled it (with an error), don't fall through to "not found"
            }

            // ObjectMacros.h is at a well-known stable path in every UE version
            var objectMacrosPath = System.IO.Path.Combine(
                detector.EngineSourceRoot, "Runtime", "CoreUObject", "Public", "UObject", "ObjectMacros.h");

            if (!await Task.Run(() => System.IO.File.Exists(objectMacrosPath)))
            {
                ShowStatusMessage($"PenguinExtension: ObjectMacros.h not found at expected path.");
                return true;
            }

            VsShellUtilities.OpenDocument(
                _package,
                objectMacrosPath,
                Guid.Empty,
                out _,
                out _,
                out IVsWindowFrame windowFrame);

            windowFrame?.Show();

            ShowStatusMessage($"PenguinExtension: Opened {word} definition in ObjectMacros.h");
            return true;
        }
    }
}
