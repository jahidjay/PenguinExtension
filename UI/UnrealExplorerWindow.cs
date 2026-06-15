using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using PenguinExtention.Models;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Unreal Explorer tool window.  Docks to the left side by default.
    /// Provides searchable browsing of all indexed Unreal symbols with navigation.
    /// </summary>
    [Guid("B4D2E6A8-1C3F-4D7B-8A9E-5F0C2B6D4A1E")]
    public sealed class UnrealExplorerWindow : ToolWindowPane
    {
        private readonly UnrealExplorerControl _control;

        public UnrealExplorerWindow() : base(null)
        {
            Caption = "Unreal Explorer";
            BitmapResourceID = 301;
            BitmapIndex = 0;

            _control = new UnrealExplorerControl();
            _control.NavigateRequested += OnNavigateRequested;

            Content = _control;
        }

        private void OnNavigateRequested(UnrealSymbol symbol)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (symbol == null || !System.IO.File.Exists(symbol.FilePath))
                return;

            var package = (AsyncPackage)Package;

            // Open the document
            VsShellUtilities.OpenDocument(
                package,
                symbol.FilePath,
                Guid.Empty,
                out _,
                out _,
                out IVsWindowFrame windowFrame);

            if (windowFrame == null) return;
            windowFrame.Show();

            // Navigate to the line
            var textView = VsShellUtilities.GetTextView(windowFrame);
            if (textView != null)
            {
                textView.SetCaretPos(symbol.LineNumber - 1, symbol.ColumnNumber);
                textView.CenterLines(symbol.LineNumber - 1, 1);
            }

            // Record usage
            Services.CacheService.Instance?.RecordUsage(symbol.Id);
        }
    }
}
