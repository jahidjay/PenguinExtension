using System;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// GUIDs and command IDs that match the VSCT command-table definitions.
    /// Keep these in sync with PenguinExtention.vsct.
    /// </summary>
    internal static class PenguinExtensionCommandIds
    {
        /// <summary>GUID of the command set (matches guidPenguinExtensionCmdSet in VSCT).</summary>
        public static readonly Guid CommandSetGuid = new Guid("E5A3C1D9-4B2F-4E8A-B6D0-7C9F1A5E3D2B");

        /// <summary>Command ID for "Go To Unreal Definition" (matches GoToUnrealDefinitionId in VSCT).</summary>
        public const int GoToUnrealDefinitionId = 0x0100;

        /// <summary>Command ID for "Open Unreal Explorer" (matches OpenUnrealExplorerId in VSCT).</summary>
        public const int OpenUnrealExplorerId = 0x0200;
    }
}
