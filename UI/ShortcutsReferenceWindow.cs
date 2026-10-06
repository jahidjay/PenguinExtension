using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Shortcuts Reference tool window — lists all PenguinExtension keyboard shortcuts.
    /// ponytail: thin wrapper.
    /// </summary>
    [Guid("D4E8F1A2-6B3C-4D5E-9A7F-2C1B0E3D4F5A")]
    public class ShortcutsReferenceWindow : ToolWindowPane
    {
        public ShortcutsReferenceWindow() : base(null)
        {
            Caption = "Penguin — Keyboard Shortcuts";
            Content = new ShortcutsReferenceControl();
        }
    }
}
