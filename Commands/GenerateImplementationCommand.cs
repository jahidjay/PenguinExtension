using System;
using System.ComponentModel.Design;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PenguinExtention.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// "Generate Function Implementation" command (Ctrl+Shift+U, I).
    /// Reads a function declaration from a .h file and generates a skeleton in the .cpp.
    /// ponytail: regex-based, handles modern C++ (auto, constexpr, noexcept, templates, override).
    /// </summary>
    internal sealed class GenerateImplementationCommand
    {
        private readonly AsyncPackage _package;

        // ponytail: one regex covers virtual/static/constexpr + return type + name + params + qualifiers
        private static readonly Regex RxFuncDecl = new Regex(
            @"^\s*(?:UFUNCTION\s*\([^)]*\)\s*)?(?:virtual\s+)?(?:static\s+)?(?:FORCEINLINE\s+)?" +
            @"((?:const\s+)?[\w:*&<>\s]+?)\s+" +        // group 1: return type
            @"(\w+)\s*" +                                 // group 2: function name
            @"\(([^)]*)\)" +                              // group 3: parameters
            @"(\s*(?:const|override|final|noexcept|PURE_VIRTUAL\s*\([^)]*\)|\s)*)" + // group 4: qualifiers
            @"\s*;",                                      // must end with semicolon (declaration, not definition)
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

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
            catch (Exception ex) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); ShowStatus("Generation failed: " + ex.Message); }
        });

        private async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var sourceView = EditorAccess.ActiveView();
            if (sourceView == null) return;
            var snapshot = sourceView.TextSnapshot;
            var line = sourceView.Caret.Position.BufferPosition.GetContainingLine();
            Microsoft.VisualStudio.Text.ITextDocument document;
            if (!sourceView.TextBuffer.Properties.TryGetProperty(typeof(Microsoft.VisualStudio.Text.ITextDocument), out document))
                throw new InvalidOperationException("Save the header before generating its implementation.");
            var header = document.FilePath;
            var ext = Path.GetExtension(header);
            if (!ext.Equals(".h", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".hpp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Generate Implementation requires a header.");
            var match = await Task.Run(() => RxFuncDecl.Match(line.GetText()));
            if (!match.Success) throw new InvalidOperationException("Cannot parse the declaration on this line.");
            // Use a parsed owner when known. Do not fabricate a class from the filename.
            var symbols = await BackendService.ExactAsync(match.Groups[2].Value, _package.DisposalToken);
            var owners = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(symbols, symbol => string.Equals(symbol.FilePath, header, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(symbol.OwnerClass)), symbol => symbol.OwnerClass)));
            if (owners.Length != 1) throw new InvalidOperationException("No unambiguous indexed owner. Index this header before generating an implementation.");
            var owner = owners[0];
            var returnType = match.Groups[1].Value.Trim();
            var name = match.Groups[2].Value;
            var parameters = match.Groups[3].Value.Trim();
            if (parameters.Contains("=") || match.Groups[4].Value.Contains("PURE_VIRTUAL"))
                throw new InvalidOperationException("Default arguments and PURE_VIRTUAL require manual implementation generation.");
            var qualifiers = match.Groups[4].Value.Replace("override", "").Replace("final", "").Trim();
            var newline = Environment.NewLine;
            var implementation = newline + returnType + " " + owner + "::" + name + "(" + parameters + ")"
                + (qualifiers.Length == 0 ? "" : " " + qualifiers) + newline + "{" + newline + "    // TODO: Implement" + newline + "}" + newline;
            var cpp = await Task.Run(() =>
            {
                var adjacent = Path.ChangeExtension(header, ".cpp");
                var alternate = adjacent.Replace(Path.DirectorySeparatorChar + "Public" + Path.DirectorySeparatorChar,
                    Path.DirectorySeparatorChar + "Private" + Path.DirectorySeparatorChar);
                return File.Exists(adjacent) ? adjacent : File.Exists(alternate) ? alternate : null;
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (sourceView.IsClosed || sourceView.TextSnapshot != snapshot) throw new InvalidOperationException("Header changed. Run generation again.");
            if (cpp == null) throw new InvalidOperationException("Create the matching .cpp in the editor first (beside the header or under Private). No files were written.");
            VsShellUtilities.OpenDocument(_package, cpp, Guid.Empty, out _, out _, out IVsWindowFrame frame);
            frame?.Show();
            var target = EditorAccess.Adapt(VsShellUtilities.GetTextView(frame));
            if (target == null) throw new InvalidOperationException("Could not open the implementation editor.");
            var targetSnapshot = target.TextSnapshot;
            var existing = await Task.Run(() => targetSnapshot.GetText().Contains(owner + "::" + name + "("));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (existing) throw new InvalidOperationException("An implementation with this name already exists; inspect overloads manually.");
            if (target.TextSnapshot != targetSnapshot) throw new InvalidOperationException("Implementation editor changed. Run generation again.");
            EditorAccess.Insert(target, targetSnapshot.Length, implementation, "Generate Unreal implementation");
            target.Caret.MoveTo(new Microsoft.VisualStudio.Text.SnapshotPoint(target.TextSnapshot, targetSnapshot.Length));
            ShowStatus("Generated " + owner + "::" + name + " in the editor (unsaved, undoable).");
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
            var sb = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            sb?.SetText(msg);
        }
    }
}
