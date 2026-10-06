using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
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
    /// "Generate Getter/Setter" command (Ctrl+Shift+U, G).
    /// Reads a UPROPERTY declaration and generates Get/Set methods in the header.
    /// ponytail: regex-based, handles const ref returns for non-primitive types.
    /// </summary>
    internal sealed class GenerateGetterSetterCommand
    {
        private readonly AsyncPackage _package;

        // ponytail: matches UPROPERTY(...) Type Name; with optional defaults
        private static readonly Regex RxUproperty = new Regex(
            @"^\s*UPROPERTY\s*\([^)]*\)\s*([\w:*&<>\s]+?)\s+(\w+)\s*(?:[=;{])",
            RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

        // ponytail: primitive types that can be returned by value cheaply
        private static readonly System.Collections.Generic.HashSet<string> PrimitiveTypes =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            {
                "bool", "int", "int8", "int16", "int32", "int64",
                "uint8", "uint16", "uint32", "uint64",
                "float", "double",
                "FName", "FVector", "FRotator", "FTransform", "FLinearColor", "FColor",
                "ECollisionChannel",
            };

        private GenerateGetterSetterCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;

            var cmdId = new CommandID(PenguinExtensionCommandIds.CommandSetGuid,
                                     PenguinExtensionCommandIds.GenerateGetterSetterId);
            commandService.AddCommand(new MenuCommand(Execute, cmdId));
        }

        public static GenerateGetterSetterCommand Instance { get; private set; }

        public static async System.Threading.Tasks.Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                Instance = new GenerateGetterSetterCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e) => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try { await ExecuteAsync(); }
            catch (Exception ex) { await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); ShowStatus("Generation failed: " + ex.Message); }
        });

        private async Task ExecuteAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var editor = EditorAccess.ActiveView();
            if (editor == null) return;
            var snapshot = editor.TextSnapshot;

            var caret = editor.Caret.Position.BufferPosition;
            var editorLine = caret.GetContainingLine();
            var line = editorLine.LineNumber;
            var lineText = editorLine.GetText();

            if (string.IsNullOrWhiteSpace(lineText))
            {
                ShowStatus("PenguinExtension: No UPROPERTY declaration on this line.");
                return;
            }

            var match = await Task.Run(() => RxUproperty.Match(lineText));
            if (!match.Success)
            {
                ShowStatus("PenguinExtension: Could not parse UPROPERTY on this line.");
                return;
            }

            var propType = match.Groups[1].Value.Trim();
            var propName = match.Groups[2].Value.Trim();

            // ponytail: derive getter/setter names
            // Strip common prefixes for clean API names
            var cleanName = propName;
            if (cleanName.StartsWith("b") && cleanName.Length > 1 && char.IsUpper(cleanName[1]))
                cleanName = cleanName.Substring(1); // bIsReady → IsReady
            else if (cleanName.StartsWith("m_"))
                cleanName = cleanName.Substring(2);
            else if (cleanName.Length > 0)
                cleanName = char.ToUpper(cleanName[0]) + cleanName.Substring(1);

            // ponytail: use const ref for non-primitive types
            bool isPrimitive = PrimitiveTypes.Contains(propType)
                            || propType.EndsWith("*"); // pointers are cheap
            var getReturnType = isPrimitive ? propType : $"const {propType}&";

            var getter = $"\t{getReturnType} Get{cleanName}() const {{ return {propName}; }}";
            var setter = $"\tvoid Set{cleanName}({(isPrimitive ? propType : $"const {propType}&")} New{cleanName}) {{ {propName} = New{cleanName}; }}";

            var insertText = $"\n{getter}\n{setter}\n";

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (editor.IsClosed || editor.TextSnapshot != snapshot)
                throw new InvalidOperationException("Header changed. Run generation again.");
            var insertionPoint = snapshot.GetLineFromLineNumber(line).End.Position;
            EditorAccess.Insert(editor, insertionPoint, insertText, "Generate Unreal getter and setter");

            ShowStatus($"PenguinExtension: Generated Get{cleanName}/Set{cleanName} for {propName}");
        }

        private void ShowStatus(string msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var sb = Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar;
            sb?.SetText(msg);
        }
    }
}
