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
using FModel.Extensions;
using System.Linq;
using System.Threading;
using System.Windows.Threading; // Dispatcher を解決するために追加

using AdonisUI.Controls; // AdonisWindow を解決するために追加

namespace FModel.Views
{
    public partial class ContentSearchView : AdonisWindow
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

            var progressVM = new ProgressWindowViewModel
            {
                Title = "ファイル内を検索中",
                IsIndeterminate = false
            };
            var progressWindow = new ProgressWindow(progressVM) { Owner = this };
            
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            
            var progress = new Progress<(int, int, string)>(value =>
            {
                progressVM.Progress = (double)value.Item1 / value.Item2 * 100;
                progressVM.Message = $"検索中: {value.Item3} ({value.Item1}/{value.Item2})";
            });
            
            progressWindow.Closing += (s, a) => _cts.Cancel();
            
            var searchTask = SearchInFilesAsync(searchTerm, progress, token);
            
            progressWindow.Show();
            
            try
            {
                var results = await searchTask;
                
                _applicationView.CUE4Parse.SearchVm.ContentSearchResults.Clear();
                foreach (var file in results)
                {
                    _applicationView.CUE4Parse.SearchVm.ContentSearchResults.Add(file);
                }
                StatusTextBlock.Text = $"{results.Count}つのファイルがヒットしました";
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "検索がキャンセルされました";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"エラーが発生しました: {ex.Message}";
            }
            finally
            {
                if (progressWindow.IsVisible)
                {
                    progressWindow.Close();
                }
            }
        }
        
        private Task<List<GameFile>> SearchInFilesAsync(string searchTerm, IProgress<(int, int, string)> progress, CancellationToken token)
        {
            return Task.Run(() =>
            {
                var filesToSearch = _applicationView.CUE4Parse.SearchVm.SearchResults.Where(f => !f.IsUePackagePayload).ToList();
                var totalFiles = filesToSearch.Count;
                var foundFiles = new System.Collections.Concurrent.ConcurrentBag<GameFile>();
                var processedFiles = 0;
                var memoryLimitPercentage = FModel.Settings.UserSettings.Default.ContentSearchMemoryLimitPercentage;
                var maxDegreeOfParallelism = Math.Max(1, (int)(Environment.ProcessorCount * (memoryLimitPercentage / 100.0)));

                Parallel.ForEach(filesToSearch, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxDegreeOfParallelism }, file =>
                {
                    token.ThrowIfCancellationRequested();
        
                    try
                    {
                        string contentString;
                        if (file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                        {
                            // GetExports()は重いので、より軽量なGetDisplayData()を使用
                            var result = _applicationView.CUE4Parse.Provider.GetLoadPackageResult(file);
                            if (!result.Package.CanDeserialize) return;
                            contentString = JsonConvert.SerializeObject(result.GetDisplayData(), Formatting.None);
                        }
                        else
                        {
                            var content = file.Read();
                            contentString = Encoding.UTF8.GetString(content);
                        }

                        if (contentString.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            foundFiles.Add(file);
                        }
                    }
                    catch (OperationCanceledException) { throw; } // キャンセルは再スロー
                    catch { /* Ignore files that can't be read or deserialized */ }
        
                    var currentProcessed = Interlocked.Increment(ref processedFiles);
                    progress.Report((currentProcessed, totalFiles, file.Name));
                });
        
                return foundFiles.OrderBy(f => f.Path).ToList();
            }, token);
        }

        private async void OnAssetDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // UI要素へのアクセスはUIスレッドで行う
            GameFile entry = null;
            Dispatcher.Invoke(() =>
            {
                var searchResultsListView = (System.Windows.Controls.ListView)this.FindName("SearchResultsListView");
                if (searchResultsListView != null && searchResultsListView.SelectedItem is GameFile selectedEntry)
                {
                    entry = selectedEntry;
                }
            });

            if (entry == null) return;

            await ApplicationService.ThreadWorkerView.Begin(cancellationToken => _applicationView.CUE4Parse.Extract(cancellationToken, entry, true));
            // MainWindow.YesWeCats.Activate() のエラーは別途調査
            // 現時点ではコメントアウトまたは適切な修正を行う
            // MainWindow.YesWeCats.Activate(); 
        }
    }
}
