using System;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// GUIDs and command IDs that match the VSCT command-table definitions.
    /// Keep these in sync with PenguinExtention.vsct.
    /// </summary>
    internal static class PenguinExtensionCommandIds
    {
        public const int CoreStatusId = 0x0700;
        public const int CoreReindexId = 0x0701;
        public const int RestartBackendId = 0x0702;
        public const int AIExplainId = 0x0710;
        public const int AIGenerateId = 0x0711;
        public const int CancelCoreJobId = 0x0712;

        /// <summary>GUID of the command set (matches guidPenguinExtensionCmdSet in VSCT).</summary>
        public static readonly Guid CommandSetGuid = new Guid("E5A3C1D9-4B2F-4E8A-B6D0-7C9F1A5E3D2B");

        /// <summary>Command ID for "Go To Unreal Definition" (matches GoToUnrealDefinitionId in VSCT).</summary>
        public const int GoToUnrealDefinitionId = 0x0100;

        /// <summary>Command ID for "Open Unreal Explorer" (matches OpenUnrealExplorerId in VSCT).</summary>
        public const int OpenUnrealExplorerId = 0x0200;

        /// <summary>Command ID for "Generate Implementation" (Alt+U, I).</summary>
        public const int GenerateImplementationId = 0x0300;

        /// <summary>Command ID for "Generate Getter/Setter" (Ctrl+Shift+U, G).</summary>
        public const int GenerateGetterSetterId = 0x0400;

        /// <summary>Command ID for "Open Symbol Inspector" (Ctrl+Shift+U, S).</summary>
        public const int OpenSymbolInspectorId = 0x0500;

        /// <summary>Command ID for "Open Shortcuts Reference" (Ctrl+Shift+U, K).</summary>
        public const int OpenShortcutsReferenceId = 0x0600;

        // ── Navigation suite (Ctrl+Shift+U, …) ──────────────────────
        public const int FindSymbolId = 0x0800;            // Y
        public const int OpenFileInSolutionId = 0x0801;    // O
        public const int ToggleHeaderSourceId = 0x0802;    // H
        public const int GoToRelatedId = 0x0803;           // R
        public const int ListMethodsId = 0x0804;           // M
        public const int GoToImplementationId = 0x0805;    // J
        public const int FindReferencesId = 0x0806;        // F
        public const int OpenDefinitionPreviewId = 0x0807; // W

        // ── Refactorings (Ctrl+Shift+U, …) ──────────────────────────
        public const int RenameSymbolId = 0x0900;          // N
        public const int AddIncludeId = 0x0901;            // A
        public const int CreateDeclarationId = 0x0902;     // C
        public const int MoveImplementationId = 0x0903;    // V
        public const int EncapsulateFieldId = 0x0904;      // P
        public const int DocumentMethodId = 0x0905;        // Q
        public const int InsertSnippetId = 0x0906;         // T (Surround With when text is selected)
        public const int ImplementVirtualMethodsId = 0x0907; // B
        public const int AddMissingCasesId = 0x0908;       // L
        public const int IntroduceVariableId = 0x0909;     // X
        public const int AddForwardDeclarationId = 0x090A; // Z

        public const int SelfTestId = 0x0A00;              // U — Penguin Self-Test
        public const int OpenColorThemeId = 0x0A01;        // Shift+T — Penguin Color Theme window
    }
}
