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

        /// <summary>Command ID for "Generate Implementation" (Ctrl+Shift+U, I).</summary>
        public const int GenerateImplementationId = 0x0300;

        /// <summary>Command ID for "Generate Getter/Setter" (Ctrl+Shift+U, G).</summary>
        public const int GenerateGetterSetterId = 0x0400;

        /// <summary>Command ID for "Open Symbol Inspector" (Ctrl+Shift+U, S).</summary>
        public const int OpenSymbolInspectorId = 0x0500;

        /// <summary>Command ID for "Open Shortcuts Reference" (Ctrl+Shift+U, K).</summary>
        public const int OpenShortcutsReferenceId = 0x0600;
    }
}
