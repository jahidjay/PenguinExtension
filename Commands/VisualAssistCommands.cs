using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.UI;

namespace PenguinExtention.Commands
{
    /// <summary>Registers the Visual Assist–style navigation and refactoring commands (IDs 0x08xx / 0x09xx).</summary>
    internal static class VisualAssistCommands
    {
        public static void Register(AsyncPackage package, OleMenuCommandService commands)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            void Add(int id, Func<Task> body) =>
                commands.AddCommand(new MenuCommand((s, e) => PenguinEditor.Run(body), new CommandID(PenguinExtensionCommandIds.CommandSetGuid, id)));

            Add(PenguinExtensionCommandIds.FindSymbolId, NavigationCommands.FindSymbolAsync);
            Add(PenguinExtensionCommandIds.OpenFileInSolutionId, NavigationCommands.OpenFileAsync);
            Add(PenguinExtensionCommandIds.ToggleHeaderSourceId, NavigationCommands.ToggleHeaderSourceAsync);
            Add(PenguinExtensionCommandIds.GoToRelatedId, NavigationCommands.GoToRelatedAsync);
            Add(PenguinExtensionCommandIds.ListMethodsId, NavigationCommands.ListMethodsAsync);
            Add(PenguinExtensionCommandIds.GoToImplementationId, NavigationCommands.GoToImplementationAsync);
            Add(PenguinExtensionCommandIds.FindReferencesId, RefactorCommands.FindReferencesAsync);
            Add(PenguinExtensionCommandIds.OpenDefinitionPreviewId, async () =>
                await package.ShowToolWindowAsync(typeof(DefinitionPreviewWindow), 0, true, package.DisposalToken));

            Add(PenguinExtensionCommandIds.RenameSymbolId, RefactorCommands.RenameAsync);
            Add(PenguinExtensionCommandIds.AddIncludeId, RefactorCommands.AddIncludeAsync);
            Add(PenguinExtensionCommandIds.CreateDeclarationId, RefactorCommands.CreateDeclarationAsync);
            Add(PenguinExtensionCommandIds.MoveImplementationId, RefactorCommands.MoveImplementationAsync);
            Add(PenguinExtensionCommandIds.EncapsulateFieldId, RefactorCommands.EncapsulateFieldAsync);
            Add(PenguinExtensionCommandIds.DocumentMethodId, RefactorCommands.DocumentMethodAsync);

            Add(PenguinExtensionCommandIds.InsertSnippetId, CodeGenCommands.InsertSnippetAsync);
            Add(PenguinExtensionCommandIds.ImplementVirtualMethodsId, CodeGenCommands.ImplementVirtualMethodsAsync);
            Add(PenguinExtensionCommandIds.AddMissingCasesId, CodeGenCommands.AddMissingCasesAsync);
            Add(PenguinExtensionCommandIds.IntroduceVariableId, CodeGenCommands.IntroduceVariableAsync);
            Add(PenguinExtensionCommandIds.AddForwardDeclarationId, CodeGenCommands.AddForwardDeclarationAsync);
            Add(PenguinExtensionCommandIds.SelfTestId, SelfTestCommand.RunAsync);
        }
    }
}
