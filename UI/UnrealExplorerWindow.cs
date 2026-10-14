using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Penguin Explorer tool window (class name and GUID kept so existing window layouts and the
    /// OpenUnrealExplorer command keep working). All UI lives in <see cref="PenguinExplorerControl"/>.
    /// </summary>
    [Guid("B4D2E6A8-1C3F-4D7B-8A9E-5F0C2B6D4A1E")]
    public sealed class UnrealExplorerWindow : ToolWindowPane
    {
        public UnrealExplorerWindow() : base(null)
        {
            Caption = "🐧 Penguin Explorer";
            Content = new PenguinExplorerControl();
        }
    }
}
