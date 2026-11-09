using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AdonisUI.Controls;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse_Conversion.Textures;
using FModel.Services;
using FModel.Settings;
using FModel.Extensions;
using Serilog;

namespace FModel.Views
{
    public partial class ReferenceChainWindow : AdonisWindow, INotifyPropertyChanged
    {
        private bool _isLoading;
        private ObservableCollection<ReferenceNode> _flatNodes;
        private ObservableCollection<NodeConnection> _connections;
        private int _searchMode;
        private string _assetTypeFilter = "All";
        private int _maxDepth = 3;
        private int _progressValue;
        private int _progressMax = 100;
        private string _progressText = "Preparing...";
        private string _searchText;
        private string _rootLabel;
        private string _searchStatus;
        private bool _hasLoaded;
        private readonly IList _selectedItems;
        private double _canvasWidth;
        private double _canvasHeight;
        private double _zoomScale = 1.0;
        private Dictionary<string, Geometry> _iconCache;
        private List<ReferenceNode> _rootNodes;
        private List<ReferenceNode> _searchResults = new List<ReferenceNode>();
        private int _currentSearchIndex = -1;
        private AnimationClock _scrollClock;
        private CancellationTokenSource _loadCts;
        private Task _loadTask;
        private bool _isClosed;

        public static readonly RoutedCommand FindCommand = new RoutedCommand();

        // ノードのカードと配置に関する寸法
        private const double NodeWidth = 250;
        private const double NodeHeight = 80;
        private const double ColumnGap = 320;      // 深さ方向（列と列）の間隔
        private const double RowGap = 48;          // 同じ列に並ぶノード同士の間隔
        private const double ColumnStagger = 36;   // 列内で互い違いにずらす量
        private const double NodeMargin = 60;      // グラフ全体の余白

        // Dragging variables
        private bool _isDraggingView;
        private Point _lastViewMousePosition;
        private bool _isDraggingNode;
        private ReferenceNode _draggingNode;
        private Point _lastNodeMousePosition;
        private FrameworkElement _draggingElement;

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsEmpty)); }
        }

        public ObservableCollection<ReferenceNode> FlatNodes
        {
            get => _flatNodes;
            set { _flatNodes = value; OnPropertyChanged(); OnPropertyChanged(nameof(NodeCount)); OnPropertyChanged(nameof(IsEmpty)); }
        }

        // ヘッダーに表示するノード数
        public int NodeCount => _flatNodes?.Count ?? 0;

        // 読み込み済みで結果が空のときだけプレースホルダーを出す
        public bool IsEmpty => _hasLoaded && !_isLoading && NodeCount == 0;

        // ヘッダーに表示する対象アセット名
        public string RootLabel
        {
            get => _rootLabel;
            private set { _rootLabel = value; OnPropertyChanged(); }
        }

        // ステータスバーに表示する検索結果
        public string SearchStatus
        {
            get => _searchStatus;
            private set { _searchStatus = value; OnPropertyChanged(); }
        }

        public ObservableCollection<NodeConnection> Connections
        {
            get => _connections;
            set { _connections = value; OnPropertyChanged(); }
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            set { _canvasWidth = value; OnPropertyChanged(); }
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            set { _canvasHeight = value; OnPropertyChanged(); }
        }

        public double ZoomScale
        {
            get => _zoomScale;
            set { _zoomScale = Math.Max(0.1, Math.Min(10.0, value)); OnPropertyChanged(); }
        }

        public int SearchMode
        {
            get => _searchMode;
            set { _searchMode = value; OnPropertyChanged(); }
        }

        public string AssetTypeFilter
        {
            get => _assetTypeFilter;
            set { _assetTypeFilter = value; OnPropertyChanged(); }
        }

        public int MaxDepth
        {
            get => _maxDepth;
            set { _maxDepth = value; OnPropertyChanged(); }
        }

        public int ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        public int ProgressMax
        {
            get => _progressMax;
            set { _progressMax = value; OnPropertyChanged(); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(); }
        }

        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(); }
        }

        public ReferenceChainWindow(IList selectedItems)
        {
            InitializeComponent();
            DataContext = this;
            _selectedItems = selectedItems;

            // アセットエクスプローラーと同じアイコンセットを使う
            _iconCache = new Dictionary<string, Geometry>();
            foreach (var resKey in new[]
                     {
                         "TextureIconAlt", "StaticMeshIconAlt", "SkeletalMeshIconAlt", "MaterialIcon",
                         "MaterialFunctionIcon", "MaterialParameterCollectionIcon", "BlueprintIcon",
                         "AnimationIconAlt", "SkeletonIcon", "PhysicsIcon", "AudioIconAlt", "VideoIcon",
                         "FontIcon", "WorldIcon", "MapIconAlt", "ClapperIcon", "FoliageIcon", "ParticleIcon",
                         "CurveIcon", "DataTableIcon", "RedirectorIcon", "ConfigIcon", "AssetIcon", "NoteIcon"
                     })
            {
                if (TryFindResource(resKey) is not Geometry g) continue;
                if (g.CanFreeze) g.Freeze();
                _iconCache[resKey] = g;
            }

            RootLabel = _selectedItems is { Count: 1 } && _selectedItems[0] is GameFile single
                ? single.Path
                : $"{_selectedItems?.Count ?? 0} assets";

            CommandBindings.Add(new CommandBinding(FindCommand, OnFindCommand));

            // ウィンドウ表示後に非同期で読み込みを開始
            Loaded += async (s, e) => await LoadReferencesAsync();
        }

        private void OnFindCommand(object sender, ExecutedRoutedEventArgs e)
        {
            SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            if (Keyboard.FocusedElement is TextBox textBox) textBox.SelectAll();
        }

        // 検索欄のクリアボタン
        private void OnSearchCleared(object sender, RoutedEventArgs e)
        {
            ClearSearchHighlights();
        }

        private void ClearSearchHighlights()
        {
            if (FlatNodes != null)
            {
                foreach (var n in FlatNodes) n.IsHighlighted = false;
            }

            _searchResults = new List<ReferenceNode>();
            _currentSearchIndex = -1;
            SearchStatus = null;
        }

        // ズーム操作（ビューの中心を保つ）
        private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomAtViewportCenter(1.2);

        private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomAtViewportCenter(1.0 / 1.2);

        private void OnResetViewClick(object sender, RoutedEventArgs e)
        {
            ZoomScale = 1.0;
            MainScrollViewer.UpdateLayout();

            var root = _rootNodes?.FirstOrDefault();
            if (root != null) CenterOnNode(root);
            else
            {
                MainScrollViewer.ScrollToHorizontalOffset(0);
                MainScrollViewer.ScrollToVerticalOffset(0);
            }
        }

        // 指定したノードが画面の中央に来るようにスクロールする
        private void CenterOnNode(ReferenceNode node)
        {
            Dispatcher.Invoke(() =>
            {
                MainScrollViewer.UpdateLayout();

                // グラフの Grid には Margin="100" が付いている
                const double margin = 100;
                var scale = ZoomScale;
                var x = node.X * scale + margin - (MainScrollViewer.ViewportWidth - NodeWidth * scale) / 2;
                var y = node.Y * scale + margin - (MainScrollViewer.ViewportHeight - NodeHeight * scale) / 2;

                MainScrollViewer.ScrollToHorizontalOffset(Math.Max(0, x));
                MainScrollViewer.ScrollToVerticalOffset(Math.Max(0, y));
            }, DispatcherPriority.Loaded);
        }

        private void ZoomAtViewportCenter(double factor)
        {
            var oldZoom = ZoomScale;
            var centerX = MainScrollViewer.HorizontalOffset + MainScrollViewer.ViewportWidth / 2;
            var centerY = MainScrollViewer.VerticalOffset + MainScrollViewer.ViewportHeight / 2;

            ZoomScale = oldZoom * factor;
            var ratio = ZoomScale / oldZoom;

            MainScrollViewer.UpdateLayout();
            MainScrollViewer.ScrollToHorizontalOffset(centerX * ratio - MainScrollViewer.ViewportWidth / 2);
            MainScrollViewer.ScrollToVerticalOffset(centerY * ratio - MainScrollViewer.ViewportHeight / 2);
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnSearchClick(sender, e);
            }
        }

        private void OnSearchClick(object sender, RoutedEventArgs e)
        {
            PerformSearch(true, true);
        }

        private void OnNextSearchClick(object sender, RoutedEventArgs e)
        {
            PerformSearch(true, false);
        }

        private void OnPrevSearchClick(object sender, RoutedEventArgs e)
        {
            PerformSearch(false, false);
        }

        private void PerformSearch(bool forward, bool newSearch)
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                ClearSearchHighlights();
                return;
            }

            if (newSearch || _searchResults.Count == 0 || _searchResults.All(n => n.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) < 0))
            {
                // 新規検索または検索条件変更
                if (FlatNodes == null) return;
                
                // 既存のハイライトをクリア
                foreach (var n in FlatNodes) n.IsHighlighted = false;

                _searchResults = FlatNodes.Where(n => n.Name.IndexOf(SearchText, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                _currentSearchIndex = -1;

                if (_searchResults.Count == 0)
                {
                    SearchStatus = $"'{SearchText}' was not found";
                    return;
                }
            }

            // 前のハイライトを消す
            if (_currentSearchIndex >= 0 && _currentSearchIndex < _searchResults.Count)
            {
                _searchResults[_currentSearchIndex].IsHighlighted = false;
            }

            // インデックス更新
            if (newSearch)
            {
                _currentSearchIndex = 0;
            }
            else
            {
                _currentSearchIndex += forward ? 1 : -1;
                if (_currentSearchIndex >= _searchResults.Count) _currentSearchIndex = 0;
                if (_currentSearchIndex < 0) _currentSearchIndex = _searchResults.Count - 1;
            }

            var node = _searchResults[_currentSearchIndex];
            node.IsHighlighted = true;
            SearchStatus = $"{_currentSearchIndex + 1} / {_searchResults.Count} — {node.Name}";

            if (node != null)
            {
                // ヒットしたノードを拡大表示
                ZoomScale = 1.5;
                
                // レイアウト更新を待ってからスクロール位置を計算・移動
                Dispatcher.Invoke(() =>
                {
                    MainScrollViewer.UpdateLayout();

                    double margin = 100;
                    double scale = ZoomScale;
                    double nodeWidth = NodeWidth;
                    double nodeHeight = NodeHeight;

                    double targetX = (node.X * scale) + margin - (MainScrollViewer.ViewportWidth - (nodeWidth * scale)) / 2;
                    double targetY = (node.Y * scale) + margin - (MainScrollViewer.ViewportHeight - (nodeHeight * scale)) / 2;

                    SmoothScrollTo(targetX, targetY);
                }, DispatcherPriority.Loaded);
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            _ = LoadReferencesAsync();
        }

        private void SmoothScrollTo(double targetX, double targetY)
        {
            if (_scrollClock != null)
            {
                _scrollClock.Controller.Stop();
            }

            var startX = MainScrollViewer.HorizontalOffset;
            var startY = MainScrollViewer.VerticalOffset;

            // アニメーションの作成
            var animation = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(500)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            _scrollClock = animation.CreateClock();
            _scrollClock.CurrentTimeInvalidated += (s, e) =>
            {
                if (_scrollClock?.CurrentProgress == null) return;
                var progress = _scrollClock.CurrentProgress.Value;
                MainScrollViewer.ScrollToHorizontalOffset(startX + (targetX - startX) * progress);
                MainScrollViewer.ScrollToVerticalOffset(startY + (targetY - startY) * progress);
            };
            _scrollClock.Controller.Begin();
        }

        private async Task LoadReferencesAsync()
        {
            // 読み込み中の更新は、前の検索を止めてから今の条件でやり直す
            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            if (_loadTask != null)
            {
                try { await _loadTask; } catch { /* 前の検索の結果は使わない */ }
                if (cts.IsCancellationRequested) return;
            }

            var task = LoadReferencesCoreAsync(cts);
            _loadTask = task;
            await task;
        }

        private async Task LoadReferencesCoreAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            IsLoading = true;
            ClearSearchHighlights();
            FlatNodes = null;
            Connections = null;

            var searchMode = SearchMode;
            var maxDepth = Math.Max(1, MaxDepth);
            var filter = AssetTypeFilter;
            ProgressMax = maxDepth;
            ProgressValue = 0;
            ProgressText = Localize("UI_RefChain_Preparing", "Preparing...");

            // Progress<T> は作成したスレッド(UI)へ通知を戻す
            var progress = new Progress<(int Depth, string Text)>(p =>
            {
                if (token.IsCancellationRequested) return;
                if (p.Depth >= 0) ProgressValue = p.Depth;
                ProgressText = p.Text;
            });

            using var job = BackgroundJobs.Start(Localize("UI_ReferenceViewer", "Reference Viewer"), cts, () => ProgressText);
            string summary = null;
            try
            {
                // UIスレッドをブロックしないようにバックグラウンドで実行
                var result = await Task.Run(() =>
                {
                    var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
                    var graph = PackageReferenceGraph.Get(provider);
                    var builder = new ReferenceTreeBuilder(this, provider, graph, filter, maxDepth, progress, token);

                    var nodes = new List<ReferenceNode>();
                    foreach (var item in _selectedItems)
                    {
                        if (item is not GameFile gameFile) continue;
                        token.ThrowIfCancellationRequested();
                        nodes.Add(searchMode == 0 ? builder.BuildDependencies(gameFile) : builder.BuildReferencers(gameFile));
                    }

                    return (Nodes: nodes, builder.FoundCount, builder.Truncated);
                }, token);

                token.ThrowIfCancellationRequested();
                _rootNodes = result.Nodes;
                LayoutNodes(_rootNodes);

                summary = string.Format(Localize(searchMode == 0 ? "UI_RefChain_SummaryDependencies" : "UI_RefChain_SummaryReferencers", "{0} assets"), result.FoundCount);
                if (result.Truncated)
                    summary += " — " + string.Format(Localize("UI_RefChain_Truncated", "stopped at {0} assets per root"), ReferenceTreeBuilder.MaxNodesPerRoot);

                // 読み込み直後は対象アセットのノードが画面中央に来るようにする
                ZoomScale = 1.0;
                var focus = _rootNodes.FirstOrDefault();
                if (focus != null) CenterOnNode(focus);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 閉じた／やり直した
                if (_loadCts == cts && !_isClosed)
                    summary = Localize("UI_RefChain_Canceled", "Canceled");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to resolve the references of {Root}", RootLabel);
                if (!_isClosed)
                    AdonisUI.Controls.MessageBox.Show(this, $"An error occurred while resolving references:\n{ex.Message}", "Error", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
            }
            finally
            {
                if (_loadCts == cts)
                {
                    _hasLoaded = true;
                    IsLoading = false;
                    SearchStatus = summary;
                }
            }
        }

        private static string Localize(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            _loadCts?.Cancel();
            base.OnClosed(e);
        }

        // UEFN の参照ビューアのように、深さごとの列に分けつつ親を子の中央に置いて上下へ広げる
        private void LayoutNodes(List<ReferenceNode> rootNodes)
        {
            var flatList = new List<ReferenceNode>();
            var connectionList = new List<NodeConnection>();
            var heights = new Dictionary<ReferenceNode, double>();

            var top = NodeMargin;
            foreach (var root in rootNodes)
            {
                var height = MeasureSubtree(root, heights);
                PlaceSubtree(root, 0, 0, top, height, flatList, connectionList, heights);
                top += height + RowGap * 2;
            }

            FlatNodes = new ObservableCollection<ReferenceNode>(flatList);
            Connections = new ObservableCollection<NodeConnection>(connectionList);

            if (flatList.Count > 0)
            {
                CanvasWidth = flatList.Max(n => n.X) + NodeWidth + NodeMargin;
                CanvasHeight = flatList.Max(n => n.Y) + NodeHeight + NodeMargin;
            }
        }

        // 「+N assets」を含む、実際に描画する子ノードの数
        private static int VisibleChildCount(ReferenceNode node)
        {
            var count = Math.Min(node.Children.Count, node.VisibleChildrenCount);
            if (node.Children.Count > node.VisibleChildrenCount) count++;
            return count;
        }

        // サブツリーが必要とする高さを先に測っておく（親を子の中央に置くため）
        private double MeasureSubtree(ReferenceNode node, Dictionary<ReferenceNode, double> heights)
        {
            if (heights.TryGetValue(node, out var cached)) return cached;

            var height = NodeHeight;
            if (VisibleChildCount(node) > 0)
            {
                var total = 0.0;
                foreach (var child in node.Children.Take(node.VisibleChildrenCount))
                    total += MeasureSubtree(child, heights) + RowGap;

                // 「+N assets」のぶん
                if (node.Children.Count > node.VisibleChildrenCount) total += NodeHeight + RowGap;

                height = Math.Max(NodeHeight, total - RowGap);
            }

            heights[node] = height;
            return height;
        }

        private void PlaceSubtree(ReferenceNode node, int depth, int indexInGroup, double top, double height,
            List<ReferenceNode> flatList, List<NodeConnection> connections, Dictionary<ReferenceNode, double> heights)
        {
            // 列をきっちり揃えず互い違いにずらして、ぎっしり並んで見えないようにする
            node.X = depth * (NodeWidth + ColumnGap) + NodeMargin + (indexInGroup % 2 == 1 ? ColumnStagger : 0);
            node.Y = top + (height - NodeHeight) / 2;
            flatList.Add(node);

            var children = node.Children.Take(node.VisibleChildrenCount).ToList();
            var hasShowMore = node.Children.Count > node.VisibleChildrenCount;
            if (children.Count == 0 && !hasShowMore) return;

            var childrenHeight = children.Sum(c => MeasureSubtree(c, heights) + RowGap)
                                 + (hasShowMore ? NodeHeight + RowGap : 0) - RowGap;
            var childTop = top + (height - childrenHeight) / 2;

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var childHeight = MeasureSubtree(child, heights);
                PlaceSubtree(child, depth + 1, i, childTop, childHeight, flatList, connections, heights);
                connections.Add(NewConnection(node, child));
                childTop += childHeight + RowGap;
            }

            if (!hasShowMore) return;

            var remaining = node.Children.Count - node.VisibleChildrenCount;
            var showMoreNode = new ReferenceNode
            {
                Name = $"+ {remaining} assets",
                Path = string.Empty,
                IsShowMore = true,
                ParentNode = node,
                X = (depth + 1) * (NodeWidth + ColumnGap) + NodeMargin + (children.Count % 2 == 1 ? ColumnStagger : 0),
                Y = childTop,
                AccentBrush = TryFindResource(AdonisUI.Brushes.DisabledForegroundBrush) as Brush ?? Brushes.DimGray
            };
            if (_iconCache.TryGetValue("NoteIcon", out var icon)) showMoreNode.IconData = icon;

            flatList.Add(showMoreNode);
            connections.Add(NewConnection(node, showMoreNode));
        }

        private static NodeConnection NewConnection(ReferenceNode source, ReferenceNode target) => new()
        {
            Source = source,
            Target = target,
            X1 = source.X + NodeWidth,
            Y1 = source.Y + NodeHeight / 2,
            X2 = target.X,
            Y2 = target.Y + NodeHeight / 2
        };

        // Zooming
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var scrollViewer = (ScrollViewer)sender;

            // Ctrlキーが押されている場合は通常のスクロール動作を行う（上下移動）
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                return;
            }

            e.Handled = true;
            if (scrollViewer.Content is not FrameworkElement grid) return;

            var mousePos = e.GetPosition(grid);
            var viewportMousePos = e.GetPosition(scrollViewer);

            var currentZoom = ZoomScale;
            var zoomFactor = 1.1;
            if (e.Delta < 0) zoomFactor = 1.0 / zoomFactor;
            
            var newZoom = Math.Max(0.1, Math.Min(10.0, currentZoom * zoomFactor));
            ZoomScale = newZoom;
            scrollViewer.UpdateLayout();

            scrollViewer.ScrollToHorizontalOffset((grid.Margin.Left + mousePos.X * newZoom) - viewportMousePos.X);
            scrollViewer.ScrollToVerticalOffset((grid.Margin.Top + mousePos.Y * newZoom) - viewportMousePos.Y);
        }

        // View Panning (Left Click)
        private bool IsPointerOnNode(object source)
        {
            var current = source as DependencyObject;
            while (current != null)
            {
                if (current is FrameworkElement fe && fe.DataContext is ReferenceNode)
                    return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        // スクロールバー上のクリックはビューのドラッグにしない（つまみの操作が反転してしまうため）
        private static bool IsPointerOnScrollBar(object source)
        {
            var current = source as DependencyObject;
            while (current != null)
            {
                if (current is System.Windows.Controls.Primitives.ScrollBar)
                    return true;
                current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        private void OnScrollViewerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsPointerOnNode(e.OriginalSource) || IsPointerOnScrollBar(e.OriginalSource)) return;

            _lastViewMousePosition = e.GetPosition(MainScrollViewer);
            _isDraggingView = true;
            MainScrollViewer.CaptureMouse();
            e.Handled = true;
        }

        private void OnScrollViewerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingView) return;

            _isDraggingView = false;
            MainScrollViewer.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void OnScrollViewerMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingView)
            {
                var currentPos = e.GetPosition(MainScrollViewer);
                var delta = currentPos - _lastViewMousePosition;
                // ドラッグ方向とスクロール方向を合わせる(逆になってたの修正)
                MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset - delta.X);
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - delta.Y);
                _lastViewMousePosition = currentPos;
                e.Handled = true;
            }
        }

        // Node Dragging
        private void OnNodeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ReferenceNode node)
            {
                if (node.IsShowMore)
                {
                    node.ParentNode.VisibleChildrenCount = node.ParentNode.Children.Count;
                    LayoutNodes(_rootNodes);
                    return;
                }

                if (e.ClickCount == 2)
                {
                    e.Handled = true;
                    if (ApplicationService.ApplicationView.CUE4Parse.Provider.TryGetGameFile(node.Path, out var gameFile))
                    {
                        ApplicationService.ThreadWorkerView.Begin(cancellationToken => ApplicationService.ApplicationView.CUE4Parse.Extract(cancellationToken, gameFile, true));
                    }
                    return;
                }

                _draggingNode = node;
                _draggingElement = element;
                _lastNodeMousePosition = e.GetPosition(MainScrollViewer);
                _isDraggingNode = true;
                element.CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnNodeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingNode = false;
            _draggingElement?.ReleaseMouseCapture();
            _draggingElement = null;
            _draggingNode = null;
        }

        private void OnOpenAssetClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is ReferenceNode node)
            {
                if (ApplicationService.ApplicationView.CUE4Parse.Provider.TryGetGameFile(node.Path, out var gameFile))
                {
                    ApplicationService.ThreadWorkerView.Begin(cancellationToken => ApplicationService.ApplicationView.CUE4Parse.Extract(cancellationToken, gameFile, true));
                }
            }
        }

        private void OnCopyPathClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem menuItem && menuItem.DataContext is ReferenceNode node)
            {
                Clipboard.SetText(node.Path);
            }
        }

        private void OnNodeMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingNode && _draggingNode != null)
            {
                var currentPos = e.GetPosition(MainScrollViewer);
                var delta = currentPos - _lastNodeMousePosition;
                
                // Adjust delta by ZoomScale
                _draggingNode.X += delta.X / ZoomScale;
                _draggingNode.Y += delta.Y / ZoomScale;

                // Expand canvas if dragged outside
                CanvasWidth = Math.Max(CanvasWidth, _draggingNode.X + 300);
                CanvasHeight = Math.Max(CanvasHeight, _draggingNode.Y + 150);

                // Update connections
                foreach (var conn in Connections)
                {
                    if (conn.Source == _draggingNode)
                    {
                        conn.X1 = _draggingNode.X + NodeWidth;
                        conn.Y1 = _draggingNode.Y + NodeHeight / 2;
                    }
                    else if (conn.Target == _draggingNode)
                    {
                        conn.X2 = _draggingNode.X;
                        conn.Y2 = _draggingNode.Y + NodeHeight / 2;
                    }
                }

                _lastNodeMousePosition = currentPos;
            }
        }

        /// <summary>
        /// 深さごとに幅優先でツリーを作る。依存先・参照元はコンテナヘッダー（IoStore）とインポートマップ（pak）から引くので、
        /// 参照元の検索でも全パッケージを読み込まない
        /// </summary>
        private sealed class ReferenceTreeBuilder
        {
            // 広く使われるアセットだとゲームのほぼ全体が並ぶので、ルートごとに打ち切る
            public const int MaxNodesPerRoot = 3000;
            private const int MaxPreviews = 100;

            private readonly ReferenceChainWindow _owner;
            private readonly AbstractVfsFileProvider _provider;
            private readonly PackageReferenceGraph _graph;
            private readonly string _filter;
            private readonly int _maxDepth;
            private readonly IProgress<(int Depth, string Text)> _progress;
            private readonly CancellationToken _token;
            private readonly ParallelOptions _parallel;
            private int _previews;

            public int FoundCount { get; private set; }
            public bool Truncated { get; private set; }

            private sealed class Item
            {
                public ReferenceNode Node;
                public ReferenceNode Parent;
                public GameFile File;
                public IPackage Package;
                public string ClassName;
                public PackageKey Key;
            }

            public ReferenceTreeBuilder(ReferenceChainWindow owner, AbstractVfsFileProvider provider, PackageReferenceGraph graph,
                string filter, int maxDepth, IProgress<(int Depth, string Text)> progress, CancellationToken token)
            {
                _owner = owner;
                _provider = provider;
                _graph = graph;
                _filter = filter;
                _maxDepth = maxDepth;
                _progress = progress;
                _token = token;
                _parallel = new ParallelOptions { CancellationToken = token };
            }

            private bool IsFiltered => !string.IsNullOrEmpty(_filter) && _filter != "All";

            public ReferenceNode BuildDependencies(GameFile root)
            {
                var format = Localize("UI_RefChain_ResolvingDependencies", "Resolving dependencies (depth {0}/{1}, {2} found)");
                var rootItem = NewItem(root, null, default);
                Load(rootItem, true);

                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Path };
                var level = new List<Item> { rootItem };
                var count = 0;
                for (var depth = 1; depth <= _maxDepth && level.Count > 0 && !Truncated; depth++)
                {
                    _progress.Report((depth - 1, string.Format(format, depth, _maxDepth, count)));

                    var dependencies = new List<GameFile>[level.Count];
                    Parallel.For(0, level.Count, _parallel, i =>
                    {
                        var item = level[i];
                        dependencies[i] = [];
                        if (item.Package == null) return;

                        dependencies[i].AddRange(_graph.GetHardDependencies(item.File, item.Package));
                        dependencies[i].AddRange(_graph.GetSoftDependencies(item.Package));
                        item.Package = null; // エクスポートまで読んだパッケージは早めに手放す
                    });

                    var next = new List<Item>();
                    for (var i = 0; i < level.Count && !Truncated; i++)
                    {
                        foreach (var dependency in dependencies[i].OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
                        {
                            if (visited.Contains(dependency.Path)) continue;
                            if (count + next.Count >= MaxNodesPerRoot)
                            {
                                Truncated = true;
                                break;
                            }

                            visited.Add(dependency.Path);
                            next.Add(NewItem(dependency, level[i].Node, default));
                        }
                    }

                    // 最後の深さでは種類が分かればよいので、パッケージを持ち続けない
                    LoadAll(next, depth < _maxDepth);
                    level = Attach(next);
                    count += level.Count;
                }

                FoundCount += count;
                return rootItem.Node;
            }

            public ReferenceNode BuildReferencers(GameFile root)
            {
                var format = Localize("UI_RefChain_SearchingReferencers", "Searching referencers (depth {0}/{1}, {2} found)");
                var rootItem = NewItem(root, null, default);
                Load(rootItem, false);

                var indexing = new ActionProgress<string>(text => _progress.Report((-1, text)));
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Path };
                var level = new List<Item> { rootItem };
                var count = 0;
                for (var depth = 1; depth <= _maxDepth && level.Count > 0 && !Truncated; depth++)
                {
                    _progress.Report((depth - 1, string.Format(format, depth, _maxDepth, count)));

                    var referencers = _graph.FindReferencers(level.Select(i => i.Key).ToList(), indexing, _token);
                    var next = new List<Item>();
                    foreach (var referencer in referencers)
                    {
                        if (visited.Contains(referencer.File.Path)) continue;
                        if (count + next.Count >= MaxNodesPerRoot)
                        {
                            Truncated = true;
                            break;
                        }

                        visited.Add(referencer.File.Path);
                        next.Add(NewItem(referencer.File, level[referencer.ParentIndex].Node, referencer.Key));
                    }

                    // 次の深さの検索はパッケージIDで足りるので、読み込みは種類と見た目のためだけ
                    LoadAll(next, false);
                    level = Attach(next);
                    count += level.Count;
                }

                FoundCount += count;
                return rootItem.Node;
            }

            private static Item NewItem(GameFile file, ReferenceNode parent, PackageKey key) => new()
            {
                Node = new ReferenceNode { Name = file.Name, Path = file.Path },
                Parent = parent,
                File = file,
                Key = new PackageKey(key.Id, file.Path)
            };

            private void LoadAll(List<Item> items, bool keepPackage)
            {
                Parallel.For(0, items.Count, _parallel, i => Load(items[i], keepPackage));
            }

            private void Load(Item item, bool keepPackage)
            {
                try
                {
                    if (!_provider.TryLoadPackage(item.File, out var package)) return;

                    item.Key = new PackageKey(item.Key.Id ?? PackageKey.From(item.File, package).Id, item.File.Path);
                    item.ClassName = GetMainClassName(package, item.File);
                    item.Node.TypeName = item.ClassName;
                    item.Node.AccentBrush = GetBrushForClass(item.ClassName);
                    item.Node.IconData = _owner.GetIconForClass(item.ClassName);

                    if (item.ClassName != null && (item.ClassName.Contains("Texture") || item.ClassName.Contains("RenderTarget")) &&
                        Interlocked.Increment(ref _previews) <= MaxPreviews)
                    {
                        item.Node.PreviewImage = GetTexturePreview(package);
                    }

                    if (keepPackage) item.Package = package;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Debug(e, "Failed to load {Path} for the reference viewer", item.File.Path);
                }
            }

            /// <summary>
            /// 種類で絞り込んだうえで親にぶら下げる。外れたものはその先も辿らない
            /// </summary>
            private List<Item> Attach(List<Item> items)
            {
                _token.ThrowIfCancellationRequested();
                var attached = new List<Item>(items.Count);
                foreach (var item in items)
                {
                    if (IsFiltered && !IsTypeMatch(item.ClassName, _filter)) continue;
                    item.Parent.Children.Add(item.Node);
                    attached.Add(item);
                }

                return attached;
            }
        }

        private sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }

        private static ImageSource GetTexturePreview(IPackage package)
        {
            try
            {
                var texture = package.GetExports().FirstOrDefault(e => e is UTexture2D) as UTexture2D;
                if (texture != null)
                {
                    var cTexture = texture.Decode(256, UserSettings.GetExportOptions());
                    if (cTexture != null)
                    {
                        using var skBitmap = cTexture.ToSkBitmap();
                        if (skBitmap.ColorType == SkiaSharp.SKColorType.Bgra8888)
                        {
                            var bitmapSource = BitmapSource.Create(skBitmap.Width, skBitmap.Height, 96, 96, PixelFormats.Bgra32, null, skBitmap.Bytes, skBitmap.RowBytes);
                            bitmapSource.Freeze();
                            return bitmapSource;
                        }

                        using var bgraBitmap = new SkiaSharp.SKBitmap(skBitmap.Width, skBitmap.Height, SkiaSharp.SKColorType.Bgra8888, skBitmap.AlphaType);
                        if (skBitmap.CopyTo(bgraBitmap))
                        {
                            var bitmapSource = BitmapSource.Create(bgraBitmap.Width, bgraBitmap.Height, 96, 96, PixelFormats.Bgra32, null, bgraBitmap.Bytes, bgraBitmap.RowBytes);
                            bitmapSource.Freeze();
                            return bitmapSource;
                        }

                        using var data = skBitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        using var stream = new MemoryStream(data.ToArray());
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Debug(e, "Failed to decode the preview of {Name}", package.Name);
            }
            return null;
        }

        private static bool IsTypeMatch(string className, string filter)
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
                "Config (.ini)" => false,
                _ => true
            };
        }

        /// <summary>
        /// アセット名と同じ（BPなら "_C" の）エクスポートのクラス。先頭のエクスポートが本体とは限らない
        /// </summary>
        private static string GetMainClassName(IPackage ipackage, GameFile file)
        {
            var name = file.NameWithoutExtension;
            switch (ipackage)
            {
                case IoPackage ioPackage when ioPackage.ExportMap.Length > 0:
                {
                    var names = ioPackage.ExportMap.Select(e => ioPackage.CreateFNameFromMappedName(e.ObjectName).Text).ToArray();
                    var index = Array.FindIndex(names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) index = Array.FindIndex(names, n => n.Equals(name + "_C", StringComparison.OrdinalIgnoreCase));
                    return ioPackage.ResolveObjectIndex(ioPackage.ExportMap[Math.Max(0, index)].ClassIndex)?.Name.Text;
                }
                case Package package when package.ExportMap.Length > 0:
                    return (package.ExportMap.FirstOrDefault(e => e.ObjectName.Text.Equals(name, StringComparison.OrdinalIgnoreCase))
                            ?? package.ExportMap.FirstOrDefault(e => e.ObjectName.Text.Equals(name + "_C", StringComparison.OrdinalIgnoreCase))
                            ?? package.ExportMap[0]).ClassName;
            }

            return null;
        }

        // アセットエクスプローラー（FileToGeometryConverter）と同じアイコン / 色を返す
        private static (string Icon, string Brush) GetStyleForClass(string className)
        {
            if (string.IsNullOrEmpty(className)) return ("AssetIcon", "NeutralBrush");

            return className switch
            {
                "Texture2D" or "TextureCube" or "TextureCubeArray" or "Texture2DArray"
                    or "VirtualTexture2D" or "TextureRenderTarget2D" => ("TextureIconAlt", "TextureBrush"),

                "StaticMesh" => ("StaticMeshIconAlt", "NeutralBrush"),
                "SkeletalMesh" => ("SkeletalMeshIconAlt", "NeutralBrush"),
                "CustomizableObject" => ("StaticMeshIconAlt", "CustomizableObjectBrush"),
                "NaniteDisplacedMesh" => ("StaticMeshIconAlt", "NaniteDisplacedMeshBrush"),

                "Material" or "MaterialInstanceConstant" => ("MaterialIcon", "MaterialBrush"),
                "MaterialFunction" => ("MaterialFunctionIcon", "MaterialBrush"),
                "MaterialParameterCollection" => ("MaterialParameterCollectionIcon", "MaterialBrush"),
                "PhysicalMaterial" => ("MaterialIcon", "NeutralBrush"),

                "WidgetBlueprintGeneratedClass" => ("BlueprintIcon", "BlueprintWidgetBrush"),
                "AnimBlueprintGeneratedClass" => ("BlueprintIcon", "BlueprintAnimBrush"),
                "RigVMBlueprintGeneratedClass" => ("BlueprintIcon", "BlueprintRigVMBrush"),
                "UserDefinedEnum" => ("BlueprintIcon", "UserDefinedEnumBrush"),
                "UserDefinedStruct" => ("BlueprintIcon", "UserDefinedStructBrush"),

                "Skeleton" => ("SkeletonIcon", "NeutralBrush"),
                "PhysicsAsset" => ("PhysicsIcon", "NeutralBrush"),

                "World" or "Level" => ("WorldIcon", "WorldBrush"),
                "MapBuildDataRegistry" => ("MapIconAlt", "BuildDataBrush"),
                "LevelSequence" => ("ClapperIcon", "LevelSequenceBrush"),
                "FoliageType_InstancedStaticMesh" or "FoliageType" => ("FoliageIcon", "FoliageBrush"),

                "ObjectRedirector" => ("RedirectorIcon", "ConfigBrush"),
                "CurveFloat" or "CurveVector" or "CurveLinearColor" or "CurveTable" => ("CurveIcon", "CurveBrush"),
                "DataTable" or "StringTable" or "DataAsset" => ("DataTableIcon", "NeutralBrush"),
                "Font" or "FontFace" => ("FontIcon", "NeutralBrush"),
                "FileMediaSource" => ("VideoIcon", "VideoBrush"),
                "NiagaraSystem" or "ParticleSystem" => ("ParticleIcon", "ParticleBrush"),

                _ when className.Contains("Blueprint") => ("BlueprintIcon", "BlueprintBrush"),
                _ when className.Contains("Anim") || className.Contains("BlendSpace") => ("AnimationIconAlt", "AnimationBrush"),
                _ when className.Contains("Sound") || className.Contains("Audio")
                       || className.Contains("Wwise") || className.Contains("Ak") => ("AudioIconAlt", "AudioBrush"),
                _ when className.Contains("Texture") || className.Contains("RenderTarget") => ("TextureIconAlt", "TextureBrush"),
                _ when className.Contains("Material") => ("MaterialIcon", "MaterialBrush"),

                _ => ("AssetIcon", "NeutralBrush")
            };
        }

        private static Brush GetBrushForClass(string className)
        {
            var key = GetStyleForClass(className).Brush;
            return Application.Current.TryFindResource(key) as Brush
                   ?? Application.Current.TryFindResource("NeutralBrush") as Brush
                   ?? Brushes.White;
        }

        private Geometry GetIconForClass(string className)
        {
            var key = GetStyleForClass(className).Icon;
            if (_iconCache.TryGetValue(key, out var g)) return g;
            return _iconCache.TryGetValue("AssetIcon", out var fallback) ? fallback : null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // ツリー表示用のデータクラス
    public class ReferenceNode : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public List<ReferenceNode> Children { get; set; } = new List<ReferenceNode>();
        public int VisibleChildrenCount { get; set; } = 10;
        public bool IsShowMore { get; set; }
        public ReferenceNode ParentNode { get; set; }

        private double _x;
        public double X { get => _x; set { _x = value; OnPropertyChanged(); } }

        private double _y;
        public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }

        private static readonly Brush DefaultAccent;

        static ReferenceNode()
        {
            DefaultAccent = Application.Current?.TryFindResource("NeutralBrush") as Brush ?? Brushes.White;
        }

        // アセット種別を表す色（アイコンと左側のバーに使用）
        private Brush _accentBrush = DefaultAccent;
        public Brush AccentBrush { get => _accentBrush; set { _accentBrush = value; OnPropertyChanged(); } }

        // アセットのクラス名（アセットエクスプローラーの Type 表示と同じ）
        private string _typeName;
        public string TypeName { get => _typeName; set { _typeName = value; OnPropertyChanged(); } }

        private Geometry _iconData;
        public Geometry IconData { get => _iconData; set { _iconData = value; OnPropertyChanged(); } }

        private ImageSource _previewImage;
        public ImageSource PreviewImage { get => _previewImage; set { _previewImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPreview)); } }

        public bool HasPreview => _previewImage != null;
        private bool _isHighlighted;
        public bool IsHighlighted { get => _isHighlighted; set { _isHighlighted = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class NodeConnection : INotifyPropertyChanged
    {
        private double _x1;
        public double X1 { get => _x1; set { _x1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurveData)); } }
        private double _y1;
        public double Y1 { get => _y1; set { _y1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurveData)); } }
        private double _x2;
        public double X2 { get => _x2; set { _x2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurveData)); } }
        private double _y2;
        public double Y2 { get => _y2; set { _y2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurveData)); } }

        public ReferenceNode Source { get; set; }
        public ReferenceNode Target { get; set; }

        public Geometry CurveData
        {
            get
            {
                var p1 = new Point(X1, Y1);
                var p2 = new Point(X2, Y2);
                var dist = Math.Abs(X2 - X1) / 2;
                if (dist < 20) dist = 20;

                var cp1 = new Point(X1 + dist, Y1);
                var cp2 = new Point(X2 - dist, Y2);

                var geometry = new PathGeometry();
                var figure = new PathFigure { StartPoint = p1, IsClosed = false };
                figure.Segments.Add(new BezierSegment(cp1, cp2, p2, true));
                geometry.Figures.Add(figure);
                geometry.Freeze();
                return geometry;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}