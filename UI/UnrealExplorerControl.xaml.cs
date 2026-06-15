using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using PenguinExtention.Models;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Code-behind for the Unreal Explorer tool window control.
    /// Handles double-click navigation and sets up the ViewModel.
    /// </summary>
    public partial class UnrealExplorerControl : UserControl
    {
        private readonly UnrealExplorerViewModel _viewModel;

        public UnrealExplorerControl()
        {
            _viewModel = new UnrealExplorerViewModel();
            DataContext = _viewModel;

            InitializeComponent();

            // Focus search box when control loads
            Loaded += (s, e) => SearchBox.Focus();
        }

        /// <summary>
        /// Raised when the user double-clicks a symbol to navigate to its source.
        /// The <see cref="UnrealExplorerWindow"/> subscribes to this and performs the actual navigation.
        /// </summary>
        public event Action<UnrealSymbol> NavigateRequested
        {
            add => _viewModel.NavigateRequested += value;
            remove => _viewModel.NavigateRequested -= value;
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ResultsList.SelectedItem is UnrealSymbol symbol)
            {
                _viewModel.NavigateToSymbolCommand.Execute(symbol);
            }
        }
    }

    /// <summary>
    /// Value converter that extracts just the filename from a full file path.
    /// Used in the ListView's File column.
    /// </summary>
    internal sealed class FileNameConverter : IValueConverter
    {
        public static readonly FileNameConverter Instance = new FileNameConverter();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string path && !string.IsNullOrEmpty(path))
                return Path.GetFileName(path);
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
