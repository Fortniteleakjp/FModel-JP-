using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;
using FModel.Services;
using FModel.Settings;
using FModel.Extensions;
using Newtonsoft.Json;

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

        public static readonly RoutedCommand FindCommand = new RoutedCommand();

        // ノードのカードと配置に関する寸法
        private const double NodeWidth = 250;
        private const double NodeHeight = 80;
        private const double ColumnGap = 190;      // 深さ方向（列と列）の間隔
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
            if (IsLoading) return;
            IsLoading = true;
            ClearSearchHighlights();
            FlatNodes = null;
            Connections = null;
            try
            {
                // UIスレッドをブロックしないようにバックグラウンドで実行
                var result = await Task.Run(() =>
                {
                    var nodes = new List<ReferenceNode>();
                    var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;

                    // 1. 選択されたアイテムごとのルートノード作成
                    foreach (var item in _selectedItems)
                    {
                        if (item is GameFile gameFile)
                        {
                            var rootNode = new ReferenceNode { Name = gameFile.Name, Path = gameFile.Path };
                            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            
                            if (SearchMode == 0)
                            {
                                BuildDependencyTree(rootNode, gameFile, MaxDepth, 0, visited, provider);
                            }
                            else
                            {
                                BuildReferencerTree(rootNode, gameFile, MaxDepth, 0, visited, provider);
                            }

                            nodes.Add(rootNode);
                        }
                    }

                    return nodes;
                });

                _rootNodes = result;
                LayoutNodes(_rootNodes);

                // 読み込み直後は対象アセットのノードが画面中央に来るようにする
                ZoomScale = 1.0;
                var focus = _rootNodes.FirstOrDefault();
                if (focus != null) CenterOnNode(focus);
            }
            catch (Exception ex)
            {
                AdonisUI.Controls.MessageBox.Show(this, $"An error occurred while resolving references:\n{ex.Message}", "Error", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
            }
            finally
            {
                _hasLoaded = true;
                IsLoading = false;
            }
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

        // 依存関係（Dependencies / Uses）ツリー構築
        private void BuildDependencyTree(ReferenceNode parentNode, GameFile file, int maxDepth, int currentDepth, ISet<string> visited, CUE4Parse.FileProvider.IFileProvider provider)
        {
            if (currentDepth >= maxDepth || !visited.Add(file.Path))
                return;

            try
            {
                IPackage ipackage = null;
                string json = null;

                if (provider is AbstractFileProvider abstractProvider)
                {
                    var result = abstractProvider.GetLoadPackageResult(file);
                    ipackage = result.Package;
                    if (ipackage != null)
                    {
                        try { json = JsonConvert.SerializeObject(result.GetDisplayData()); } catch { }
                    }
                }
                else if (provider.TryLoadPackage(file.Path, out ipackage))
                {
                    try { json = JsonConvert.SerializeObject(ipackage.GetExports()); } catch { }
                }

                if (ipackage == null) return;

                var className = GetPackageClassName(ipackage);
                parentNode.AccentBrush = GetBrushForClass(className);
                parentNode.IconData = GetIconForClass(className);
                parentNode.TypeName = className;
                if (className.Contains("Texture") || className.Contains("RenderTarget"))
                {
                    parentNode.PreviewImage = GetTexturePreview(ipackage);
                }

                var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // パッケージからインポート（依存先）を取得
                if (ipackage is IoPackage ioPackage)
                {
                    foreach (var importIndex in ioPackage.ImportMap)
                    {
                        var resolved = ioPackage.ResolveObjectIndex(importIndex);
                        if (resolved != null && resolved.Class != null && resolved.Class.Name.Text == "Package") dependencies.Add(resolved.Name.Text);
                    }
                }
                else if (ipackage is Package package)
                {
                    foreach (var import in package.ImportMap)
                    {
                        if (import.ClassName.ToString() == "Package") dependencies.Add(import.ObjectName.ToString());
                    }
                }

                // Soft References (JSONから取得)
                if (!string.IsNullOrEmpty(json))
                {
                    var matches = Regex.Matches(json, "\"(?:AssetPathName|ObjectPath)\"\\s*:\\s*\"([^\"]+)\"");
                    foreach (Match match in matches)
                    {
                        if (match.Success && match.Groups.Count > 1)
                        {
                            var path = match.Groups[1].Value;
                            var dotIndex = path.LastIndexOf('.');
                            if (dotIndex > 0) path = path.Substring(0, dotIndex);
                            dependencies.Add(path);
                        }
                    }
                }

                foreach (var depPath in dependencies)
                {
                    var lookupPath = depPath.StartsWith("/") ? depPath.Substring(1) : depPath;
                    // パス解決（拡張子なしのパスからGameFileを探す）
                    if (provider.TryGetGameFile(lookupPath + ".uasset", out var depFile))
                    {
                        // フィルタリング: 子ノードを追加する前にタイプをチェック
                        if (!string.IsNullOrEmpty(AssetTypeFilter) && AssetTypeFilter != "All")
                        {
                            if (provider.TryLoadPackage(depFile.Path, out var depPackage))
                            {
                                var depClassName = GetPackageClassName(depPackage);
                                if (!IsTypeMatch(depClassName, AssetTypeFilter)) continue;
                            }
                            else
                                continue; // パッケージが読み込めない場合はスキップ（安全策）
                        }

                        var childNode = new ReferenceNode { Name = depFile.Name, Path = depFile.Path };
                        parentNode.Children.Add(childNode);
                        BuildDependencyTree(childNode, depFile as GameFile, maxDepth, currentDepth + 1, visited, provider);
                    }
                }
            }
            catch { }
        }

        // 参照元（Referencers）ツリー構築
        private void BuildReferencerTree(ReferenceNode parentNode, GameFile file, int maxDepth, int currentDepth, ISet<string> visited, CUE4Parse.FileProvider.IFileProvider provider)
        {
            if (currentDepth >= maxDepth || !visited.Add(file.Path))
                return;

            try
            {
                // 親ノード（この場合は参照されている側）のスタイル設定
                if (provider.TryLoadPackage(file.Path, out var ipackage))
                {
                    var className = GetPackageClassName(ipackage);
                    parentNode.AccentBrush = GetBrushForClass(className);
                    parentNode.IconData = GetIconForClass(className);
                    parentNode.TypeName = className;
                    if (className.Contains("Texture") || className.Contains("RenderTarget"))
                    {
                        parentNode.PreviewImage = GetTexturePreview(ipackage);
                    }
                }

                // このファイルをインポートしているファイル（参照元）を探す
                // 注意: インデックスがないため、全ファイルスキャンが必要となり非常に重い処理です
                var referencers = FindReferencers(file.PathWithoutExtension, provider);

                foreach (var refFile in referencers)
                {
                    // フィルタリング
                    if (!string.IsNullOrEmpty(AssetTypeFilter) && AssetTypeFilter != "All")
                    {
                        if (provider.TryLoadPackage(refFile.Path, out var refPackage))
                        {
                            var refClassName = GetPackageClassName(refPackage);
                            if (!IsTypeMatch(refClassName, AssetTypeFilter)) continue;
                        }
                        else
                            continue;
                    }

                    var childNode = new ReferenceNode { Name = refFile.Name, Path = refFile.Path };
                    parentNode.Children.Add(childNode);
                    BuildReferencerTree(childNode, refFile, maxDepth, currentDepth + 1, visited, provider);
                }
            }
            catch { }
        }

        private ImageSource GetTexturePreview(IPackage package)
        {
            try
            {
                var texture = package.GetExports().FirstOrDefault(e => e is UTexture2D) as UTexture2D;
                if (texture != null)
                {
                    var cTexture = texture.Decode(256, UserSettings.Default.CurrentDir.TexturePlatform);
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
            catch { }
            return null;
        }

        private IEnumerable<GameFile> FindReferencers(string targetPackagePath, CUE4Parse.FileProvider.IFileProvider provider)
        {
            var results = new System.Collections.Concurrent.ConcurrentBag<GameFile>();
            var targetPath = targetPackagePath.StartsWith("/") ? targetPackagePath : "/" + targetPackagePath;

            // 並列処理で全ファイルをスキャン
            Parallel.ForEach(provider.Files, (kvp) =>
            {
                var file = kvp.Value;
                // アセットファイル以外はスキップ
                if (!file.Name.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && 
                    !file.Name.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    return;

                try
                {
                    if (provider.TryLoadPackage(file.Path, out var package))
                    {
                        // パッケージのインポートリストをチェック
                        if (package is Package pkg)
                        {
                            foreach (var import in pkg.ImportMap)
                            {
                                if (import.ObjectName.Text.Contains(targetPath.Substring(targetPath.LastIndexOf('/') + 1)))
                                {
                                    results.Add(file as GameFile);
                                    break;
                                }
                            }
                        }
                        // IoPackage等の対応は省略（必要に応じて追加）
                    }
                }
                catch { }
            });

            return results;
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
                "Config (.ini)" => false,
                _ => true
            };
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