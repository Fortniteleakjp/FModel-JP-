using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AdonisUI.Controls;

namespace FModel.Views
{
    public partial class ExportCartWindow : AdonisWindow
    {
        private readonly ObservableCollection<string> _allPaths = new();

        public ObservableCollection<string> FilteredPaths { get; } = new();

        public Func<string, Task>? OpenAssetAsync { get; set; }
        public Action<IReadOnlyList<string>>? RemoveAssetsAction { get; set; }

        public ExportCartWindow(IEnumerable<string> paths)
        {
            InitializeComponent();
            DataContext = this;

            foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _allPaths.Add(path);
            }

            ApplyFilter(string.Empty);
        }

        private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFilter(SearchTextBox.Text);
        }

        private async void OnPathDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PathsListBox.SelectedItem is not string path || OpenAssetAsync == null)
                return;

            await OpenAssetAsync(path);
        }

        private void OnRemoveSelectedClick(object sender, RoutedEventArgs e)
        {
            var selectedPaths = PathsListBox.SelectedItems.Cast<string>().ToArray();
            if (selectedPaths.Length <= 0)
                return;

            RemoveAssetsAction?.Invoke(selectedPaths);

            foreach (var selectedPath in selectedPaths)
            {
                var existing = _allPaths.FirstOrDefault(path => path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    _allPaths.Remove(existing);
                }
            }

            ApplyFilter(SearchTextBox.Text);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ApplyFilter(string filter)
        {
            var normalizedFilter = filter?.Trim();
            FilteredPaths.Clear();

            foreach (var path in _allPaths)
            {
                if (string.IsNullOrEmpty(normalizedFilter) || path.Contains(normalizedFilter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredPaths.Add(path);
                }
            }

            CountTextBlock.Text = $"{FilteredPaths.Count} / {_allPaths.Count}";
        }
    }
}
