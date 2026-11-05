using System.Windows;
using System.Windows.Controls;
using PenguinExtention.Services;

namespace PenguinExtention.UI
{
    /// <summary>Static shortcut list plus the donate link.</summary>
    public partial class ShortcutsReferenceControl : UserControl
    {
        public ShortcutsReferenceControl()
        {
            InitializeComponent();
        }

        private void Donate_Click(object sender, RoutedEventArgs e) => PenguinLinks.OpenDonate();
    }
}
