using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Symbol Inspector tool window — shows detailed documentation for the symbol under cursor.
    /// ponytail: thin wrapper, all logic lives in the WPF control.
    /// </summary>
    [Guid("B3A7C9E1-5D4F-4A2B-8E6C-1F0D3A5B7C9E")]
    public class SymbolInspectorWindow : ToolWindowPane
    {
        public SymbolInspectorWindow() : base(null)
        {
            Caption = "Penguin — Symbol Inspector";
            Content = new SymbolInspectorControl();
        }
    }
}
