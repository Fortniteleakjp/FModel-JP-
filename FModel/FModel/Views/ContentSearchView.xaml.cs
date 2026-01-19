using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.ViewModels;
using Ookii.Dialogs.Wpf;
using Newtonsoft.Json;
using FModel.Extensions;
using System.Linq;
using System.Threading;
using System.Windows.Threading; // Dispatcher を解決するために追加

using CUE4Parse.UE4.Assets;
using AdonisUI.Controls; // AdonisWindow を解決するために追加

namespace FModel.Views
{
    public partial class ContentSearchView : AdonisWindow
    {
        private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
        public ObservableCollection<string> SearchFolders { get; } = new ObservableCollection<string>();
        private CancellationTokenSource _cts;

        public static readonly DependencyProperty FilterStatusTextProperty =
            DependencyProperty.Register(nameof(FilterStatusText), typeof(string), typeof(ContentSearchView), new PropertyMetadata("検索フォルダフィルタ (すべてのファイルを検索)"));

        public string FilterStatusText
        {
            get => (string)GetValue(FilterStatusTextProperty);
            set => SetValue(FilterStatusTextProperty, value);
        }

        public static readonly DependencyProperty AssetTypeFilterProperty =
            DependencyProperty.Register(nameof(AssetTypeFilter), typeof(string), typeof(ContentSearchView), new PropertyMetadata("All"));

        public string AssetTypeFilter
        {
            get => (string)GetValue(AssetTypeFilterProperty);
            set => SetValue(AssetTypeFilterProperty, value);
        }

        public static readonly DependencyProperty MinFileSizeProperty =
            DependencyProperty.Register(nameof(MinFileSize), typeof(double), typeof(ContentSearchView), new PropertyMetadata(0.0));

        public double MinFileSize
        {
            get => (double)GetValue(MinFileSizeProperty);
            set => SetValue(MinFileSizeProperty, value);
        }

        public static readonly DependencyProperty MaxFileSizeProperty =
            DependencyProperty.Register(nameof(MaxFileSize), typeof(double), typeof(ContentSearchView), new PropertyMetadata(0.0));

        public double MaxFileSize
        {
            get => (double)GetValue(MaxFileSizeProperty);
            set => SetValue(MaxFileSizeProperty, value);
        }

        public static readonly DependencyProperty FileNameFilterProperty =
            DependencyProperty.Register(nameof(FileNameFilter), typeof(string), typeof(ContentSearchView), new PropertyMetadata(string.Empty));

        public string FileNameFilter
        {
            get => (string)GetValue(FileNameFilterProperty);
            set => SetValue(FileNameFilterProperty, value);
        }

        public ContentSearchView()
        {
            InitializeComponent();
            DataContext = _applicationView; // ViewModelをDataContextに設定
            SearchFolders.CollectionChanged += OnSearchFoldersChanged;
            UpdateFilterStatusText(); // 初期状態を設定
            // XAMLでバインドされるため、コードでの設定は不要
            // this.FolderFilterListBox.ItemsSource = SearchFolders; 
            // this.AddFolderButton.Click += OnAddFolderClick; // XAMLでClickイベントを設定するため不要
            // this.RemoveFolderButton.Click += OnRemoveFolderClick; // XAMLでClickイベントを設定するため不要
            SearchTextBox.Focus();
        }

        private void OnSearchFoldersChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateFilterStatusText();
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
            
            var assetType = AssetTypeFilter;
            var minSize = MinFileSize;
            var maxSize = MaxFileSize;
            var fileName = FileNameFilter;

            var searchTask = SearchInFilesAsync(searchTerm, assetType, minSize, maxSize, fileName, progress, token);
            
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
        
        private Task<List<GameFile>> SearchInFilesAsync(string searchTerm, string assetTypeFilter, double minSize, double maxSize, string fileNameFilter, IProgress<(int, int, string)> progress, CancellationToken token)
        {
            return Task.Run(() =>
            {
                var allFiles = _applicationView.CUE4Parse.SearchVm.SearchResults.Where(f => !f.IsUePackagePayload);

                var query = allFiles.AsEnumerable();

                if (SearchFolders.Any())
                    query = query.Where(f => SearchFolders.Any(folder => f.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));

                if (!string.IsNullOrEmpty(fileNameFilter))
                    query = query.Where(f => f.Name.Contains(fileNameFilter, StringComparison.OrdinalIgnoreCase));

                if (minSize > 0) query = query.Where(f => f.Size >= minSize * 1024 * 1024);
                if (maxSize > 0) query = query.Where(f => f.Size <= maxSize * 1024 * 1024);

                var filesToSearch = query.ToList();

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

                            if (!string.IsNullOrEmpty(assetTypeFilter) && assetTypeFilter != "All")
                            {
                                var className = GetPackageClassName(result.Package);
                                if (!IsTypeMatch(className, assetTypeFilter)) return;
                            }

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
            // MainWindow.YesWeCats.Activate(); 
        }

        private void OnAddFolderClick(object sender, RoutedEventArgs e)
        {
            var folderPath = FolderInputTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(folderPath) && !SearchFolders.Contains(folderPath))
            {
                SearchFolders.Add(folderPath);
                FolderInputTextBox.Clear();
            }
        }

        private void OnFolderInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnAddFolderClick(sender, e);
            }
        }
        private void OnRemoveFolderClick(object sender, RoutedEventArgs e)
        {
            // FolderFilterListBoxから選択されたアイテムを取得し、SearchFoldersから削除
            var selectedItems = FolderFilterListBox.SelectedItems.Cast<string>().ToList();
            foreach (var item in selectedItems)
            {
                SearchFolders.Remove(item);
            }
        }

        private void UpdateFilterStatusText()
        {
            if (SearchFolders.Any())
            {
                FilterStatusText = $"検索フォルダフィルタ ({SearchFolders.Count}個のフォルダを指定中)";
            }
            else
            {
                FilterStatusText = "検索フォルダフィルタ (すべてのファイルを検索)";
            }
        }

        private string GetPackageClassName(IPackage ipackage)
        {
            if (ipackage == null) return null;

            if (ipackage is IoPackage ioPackage)
            {
                if (ioPackage.ExportMap.Length > 0)
                {
                    var entry = ioPackage.ExportMap[0];
                    var resolved = ioPackage.ResolveObjectIndex(entry.ClassIndex);
                    return resolved != null ? resolved.Name.Text : null;
                }
            }
            else if (ipackage is Package package)
            {
                if (package.ExportMap.Length > 0)
                {
                    return package.ExportMap[0].ClassName.ToString();
                }
            }
            return null;
        }

        private bool IsTypeMatch(string className, string filter)
        {
            if (string.IsNullOrEmpty(className)) return false;
            if (filter == "All") return true;

            return filter switch
            {
                "Texture" => className.Contains("Texture") || className.Contains("RenderTarget"),
                "Material" => className.Contains("Material"),
                "Mesh" => className.Contains("StaticMesh") || className.Contains("SkeletalMesh"),
                "Animation" => className.Contains("Anim") || className.Contains("BlendSpace") || className.Contains("Skeleton"),
                "Sound" => className.Contains("Sound") || className.Contains("Audio"),
                "Blueprint" => className.Contains("Blueprint"),
                _ => className.Contains(filter, StringComparison.OrdinalIgnoreCase)
            };
        }
    }
}
