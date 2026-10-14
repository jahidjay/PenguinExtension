using System;
using System.ComponentModel.Design;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PenguinExtention.Core;
using PenguinExtention.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// "Generate Function Implementation" command (Alt+U, I).
    /// Reads a function declaration from a .h file and generates a skeleton in the .cpp.
    /// ponytail: regex-based, handles modern C++ (auto, constexpr, noexcept, templates, override).
    /// </summary>
    internal sealed class GenerateImplementationCommand
    {
        private readonly AsyncPackage _package;

        private GenerateImplementationCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;

            var cmdId = new CommandID(PenguinExtensionCommandIds.CommandSetGuid,
                                     PenguinExtensionCommandIds.GenerateImplementationId);
            commandService.AddCommand(new MenuCommand(Execute, cmdId));
        }

        public static GenerateImplementationCommand Instance { get; private set; }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                Instance = new GenerateImplementationCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await ExecuteAsync(); }
            catch (Exception ex) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); PenguinEditor.Fail("Generation failed: " + ex.Message); }
        });

        private Task ExecuteAsync() => CreateImplementationAsync(EditorAccess.ActiveView());

        /// <summary>
        /// VA "Create Implementation": the declaration around the caret (UFUNCTION line above, multi-line parameters,
        /// default arguments …) gets a definition in the partner .cpp, and the caret lands inside the new body.
        /// When the definition already exists, jumps to it instead. Shared with the Ctrl+. lightbulb.
        /// </summary>
        internal static async Task CreateImplementationAsync(Microsoft.VisualStudio.Text.Editor.ITextView sourceView)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (sourceView == null) return;
            var snapshot = sourceView.TextSnapshot;
            int caret = sourceView.Caret.Position.BufferPosition.Position;
            var header = PenguinEditor.FilePathOf(sourceView);
            if (string.IsNullOrEmpty(header)) throw new InvalidOperationException("Save the header before creating its implementation.");
            if (!HeaderSourceResolver.IsHeader(header))
                throw new InvalidOperationException("Create Implementation works on a declaration in a header (.h). Use Create Declaration (Alt+U, C) in a .cpp.");

            var text = snapshot.GetText();
            var decl = await Task.Run(() => CodeContext.DeclarationAtCaret(text, caret));
            if (decl == null)
                throw new InvalidOperationException("Put the caret on a function declaration such as  void MyFunction(int32 Value);  (any line of it, or its UFUNCTION line).");
            if (decl.HasInlineBody) throw new InvalidOperationException(decl.Name + " already has an inline body. Use Move Implementation to Source (Alt+U, V).");
            if (decl.IsPureVirtual) throw new InvalidOperationException(decl.Name + " is pure virtual (= 0); it has no implementation in this class.");
            if (decl.IsDefaultedOrDeleted) throw new InvalidOperationException(decl.Name + " is = default / = delete; nothing to implement.");
            if (decl.ImplementationName != decl.Name && decl.UFunctionSpecifiers != null && decl.UFunctionSpecifiers.Contains("BlueprintImplementableEvent"))
                throw new InvalidOperationException(decl.Name + " is a BlueprintImplementableEvent; it is implemented in Blueprint, not C++.");

            var cpp = await Task.Run(() => HeaderSourceResolver.Partner(header));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (cpp == null)
                throw new InvalidOperationException("No .cpp found for " + Path.GetFileName(header) + " (looked beside it and in the matching Private folder). Create " +
                    Path.ChangeExtension(Path.GetFileName(header), ".cpp") + " first.");

            var target = PenguinEditor.Open(cpp);
            if (target == null) throw new InvalidOperationException("Could not open " + cpp + ".");
            var targetSnapshot = target.TextSnapshot;
            var targetText = targetSnapshot.GetText();
            int existing = await Task.Run(() => CodeContext.FindDefinition(targetText, decl));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var label = (decl.Owner == null ? "" : decl.Owner + "::") + decl.ImplementationName;
            if (existing >= 0)
            {
                MoveCaret(target, existing);
                PenguinEditor.Status(label + " already exists — jumped to it.");
                return;
            }
            if (target.TextSnapshot != targetSnapshot) throw new InvalidOperationException("The .cpp changed while generating. Run it again.");

            var newline = targetText.Contains("\r\n") || targetText.Length == 0 ? "\r\n" : "\n";
            var indent = CountIndent(targetText);
            var definition = CodeContext.DefinitionText(decl, newline, indent, out int caretOffset);
            int insertAt = targetSnapshot.Length;
            if (targetText.Length > 0 && !targetText.EndsWith("\n")) definition = newline + definition;
            if (targetText.Length > 0 && !targetText.EndsWith("\n")) caretOffset += newline.Length;
            EditorAccess.Insert(target, insertAt, definition, "Create Implementation " + label);
            MoveCaret(target, insertAt + caretOffset);
            PenguinEditor.Status("Created " + label + " in " + Path.GetFileName(cpp) + " (unsaved, Ctrl+Z to undo).");
        }

        private static string CountIndent(string text)
        {
            int tabs = 0, spaces = 0;
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("\t")) tabs++;
                else if (line.StartsWith("    ")) spaces++;
            }
            return spaces > tabs ? "    " : "\t";
        }

        private static void MoveCaret(Microsoft.VisualStudio.Text.Editor.ITextView view, int position)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var point = new Microsoft.VisualStudio.Text.SnapshotPoint(view.TextSnapshot, Math.Min(position, view.TextSnapshot.Length));
            view.Caret.MoveTo(point);
            view.ViewScroller.EnsureSpanVisible(new Microsoft.VisualStudio.Text.SnapshotSpan(point, 0),
                Microsoft.VisualStudio.Text.Editor.EnsureSpanVisibleOptions.AlwaysCenter);
            (view as Microsoft.VisualStudio.Text.Editor.IWpfTextView)?.VisualElement.Focus();
        }

        private static string GetBufferFilePath(IVsTextLines buffer)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (buffer is IPersistFileFormat pff)
            {
                pff.GetCurFile(out string path, out _);
                return path;
            }
            return null;
        }

        private void ShowStatus(string msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            PenguinExtention.Services.PenguinLog.Info("Command", msg);
            var sb = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            sb?.SetText(msg);
        }
    }
}
