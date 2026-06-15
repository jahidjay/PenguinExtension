using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.UI
{
    /// <summary>
    /// MVVM ViewModel for the Unreal Explorer tool window.
    /// Provides debounced search, kind-based filtering, and navigation commands.
    /// </summary>
    internal sealed class UnrealExplorerViewModel : INotifyPropertyChanged
    {
        private string _searchText = string.Empty;
        private bool _filterClasses = true;
        private bool _filterStructs = true;
        private bool _filterEnums = true;
        private bool _filterFunctions = true;
        private bool _filterProperties = true;
        private bool _filterEngineSymbols = true;
        private bool _filterProjectSymbols = true;
        private string _statusText = "Ready";
        private CancellationTokenSource _searchCts;

        public ObservableCollection<UnrealSymbol> FilteredSymbols { get; } = new ObservableCollection<UnrealSymbol>();

        // ── Search text (debounced 150ms) ───────────────────────────

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value) return;
                _searchText = value;
                OnPropertyChanged();
                DebouncedSearch();
            }
        }

        // ── Filter toggles ──────────────────────────────────────────

        public bool FilterClasses     { get => _filterClasses;     set { _filterClasses = value;     OnPropertyChanged(); DebouncedSearch(); } }
        public bool FilterStructs     { get => _filterStructs;     set { _filterStructs = value;     OnPropertyChanged(); DebouncedSearch(); } }
        public bool FilterEnums       { get => _filterEnums;       set { _filterEnums = value;       OnPropertyChanged(); DebouncedSearch(); } }
        public bool FilterFunctions   { get => _filterFunctions;   set { _filterFunctions = value;   OnPropertyChanged(); DebouncedSearch(); } }
        public bool FilterProperties  { get => _filterProperties;  set { _filterProperties = value;  OnPropertyChanged(); DebouncedSearch(); } }
        public bool FilterEngineSymbols  { get => _filterEngineSymbols;  set { _filterEngineSymbols = value;  OnPropertyChanged(); DebouncedSearch(); } }
        public bool FilterProjectSymbols { get => _filterProjectSymbols; set { _filterProjectSymbols = value; OnPropertyChanged(); DebouncedSearch(); } }

        // ── Status ──────────────────────────────────────────────────

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        // ── Navigation command ──────────────────────────────────────

        public ICommand NavigateToSymbolCommand { get; }

        /// <summary>Raised when the user wants to navigate to a symbol.  The package handles the actual navigation.</summary>
        public event Action<UnrealSymbol> NavigateRequested;

        // ── Constructor ─────────────────────────────────────────────

        public UnrealExplorerViewModel()
        {
            NavigateToSymbolCommand = new RelayCommand<UnrealSymbol>(sym =>
            {
                if (sym != null)
                    NavigateRequested?.Invoke(sym);
            });

            // Listen for cache ready events
            if (CacheService.Instance != null)
            {
                CacheService.Instance.CacheReady += (s, e) =>
                {
                    UpdateStatus();
                    DebouncedSearch();
                };
            }

            UpdateStatus();
        }

        // ── Search logic ────────────────────────────────────────────

        private async void DebouncedSearch()
        {
            // Cancel any previous search
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            try
            {
                await Task.Delay(150, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                ExecuteSearch();
            }
            catch (TaskCanceledException) { }
        }

        private void ExecuteSearch()
        {
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded)
            {
                System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
                {
                    FilteredSymbols.Clear();
                    StatusText = "Cache not loaded";
                });
                return;
            }

            List<UnrealSymbol> results;

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                // Show recently used / top symbols when search is empty
                results = cache.GetAllSymbols(limit: 200);
            }
            else
            {
                results = cache.Search(_searchText, kindFilter: null, limit: 500);
            }

            // Apply kind filters
            var allowedKinds = GetAllowedKinds();
            results = results.Where(s =>
            {
                if (!allowedKinds.Contains(s.Kind)) return false;
                if (s.IsEngineSymbol && !_filterEngineSymbols) return false;
                if (!s.IsEngineSymbol && !_filterProjectSymbols) return false;
                return true;
            }).Take(300).ToList();

            // Update on UI thread
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                FilteredSymbols.Clear();
                foreach (var sym in results)
                    FilteredSymbols.Add(sym);

                var total = cache.SymbolCount;
                StatusText = $"Showing {results.Count} of {total:N0} indexed symbols";
            });
        }

        private HashSet<UnrealSymbolKind> GetAllowedKinds()
        {
            var kinds = new HashSet<UnrealSymbolKind>();
            if (_filterClasses)    { kinds.Add(UnrealSymbolKind.Class); kinds.Add(UnrealSymbolKind.Interface); }
            if (_filterStructs)    kinds.Add(UnrealSymbolKind.Struct);
            if (_filterEnums)      { kinds.Add(UnrealSymbolKind.Enum); kinds.Add(UnrealSymbolKind.EnumValue); }
            if (_filterFunctions)  { kinds.Add(UnrealSymbolKind.Function); kinds.Add(UnrealSymbolKind.Delegate); }
            if (_filterProperties) kinds.Add(UnrealSymbolKind.Property);
            kinds.Add(UnrealSymbolKind.Macro); // always show macros
            return kinds;
        }

        private void UpdateStatus()
        {
            var cache = CacheService.Instance;
            if (cache == null)
                StatusText = "Initializing...";
            else if (!cache.IsLoaded)
                StatusText = "Loading cache...";
            else
                StatusText = $"Indexed: {cache.SymbolCount:N0} symbols";
        }

        // ── INotifyPropertyChanged ──────────────────────────────────

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Simple ICommand implementation for MVVM binding.</summary>
    internal sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke((T)parameter) ?? true;
        public void Execute(object parameter) => _execute((T)parameter);
    }
}
