using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Tools → Options → PenguinExtension → Color Theme: the same editor as the Penguin Color Theme window.
    /// Edits apply and save immediately (like the window), so OK and Cancel just close the dialog.
    /// </summary>
    [Guid("6B1E2F7A-3C4D-4E8B-9A1F-2D5C7E9B0A41")]
    public class ColorThemeOptionsPage : UIElementDialogPage
    {
        private ColorThemeControl _control;

        protected override UIElement Child => _control ?? (_control = new ColorThemeControl());
    }
}
