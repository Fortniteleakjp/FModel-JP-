using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.ViewModels;
using Newtonsoft.Json;
using System.Linq;
using System.Threading;

namespace FModel.Views
{
    public partial class ContentSearchView
    {
        private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
        private CancellationTokenSource _cts;

        public ContentSearchView()
        {
            InitializeComponent();
            DataContext = _applicationView;
            SearchTextBox.Focus();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnSearchClick(sender, e);
            }
        }

        private async void OnSearchClick(object sender, RoutedEventArgs e)
        {
            var searchTerm = SearchTextBox.Text;
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return;
            }

            _applicationView.CUE4Parse.SearchVm.ContentSearchResults.Clear();
            StatusTextBlock.Text = "Searching...";

            var progressViewModel = new ProgressWindowViewModel();
            var progressWindow = new ProgressWindow(progressViewModel) { Owner = this };

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            progressViewModel.CancelCommand.CanExecuteChanged += (s, ev) =>
            {
                if (!progressViewModel.CancelCommand.CanExecute(null))
                {
                    _cts.Cancel();
                }
            };

            var searchTask = Task.Run(() =>
            {
                var filesToSearch = _applicationView.CUE4Parse.SearchVm.SearchResults.Where(f => !f.IsUePackagePayload).ToList();
                var totalFiles = filesToSearch.Count;
                var foundFiles = new List<GameFile>();

                for (int i = 0; i < totalFiles; i++)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    var file = filesToSearch[i];
                    try
                    {
                        string contentString;
                        if (file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || 
                            file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                // For package files, load them and serialize to JSON for searching
                                var package = _applicationView.CUE4Parse.Provider.LoadPackage(file);
                                if (!package.CanDeserialize) continue;
                                contentString = JsonConvert.SerializeObject(package.GetExports(), Formatting.None);
                            }
                            catch (Exception)
                            {
                                // If serialization fails, just skip this file.
                                continue;
                            }                        }
                        else
                        {
                            // For other files, read as raw text
                            var content = file.Read();
                            contentString = Encoding.UTF8.GetString(content);
                        }

                        if (contentString.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foundFiles.Add(file);
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore files that can't be read
                    }

                    progressViewModel.UpdateProgress(i + 1, totalFiles, $"Searching in {file.Name}...");
                }

                return foundFiles;
            }, token);

            progressWindow.ShowDialog(); // Show progress window modally

            var results = await searchTask;

            foreach (var file in results)
            {
                _applicationView.CUE4Parse.SearchVm.ContentSearchResults.Add(file);
            }

            StatusTextBlock.Text = $"{results.Count}つのファイルがヒットしました";
        }

        private async void OnAssetDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SearchResultsListView.SelectedItem is not GameFile entry) return;

            await ApplicationService.ThreadWorkerView.Begin(cancellationToken => _applicationView.CUE4Parse.Extract(cancellationToken, entry, true));
            MainWindow.YesWeCats.Activate();
        }
    }
}