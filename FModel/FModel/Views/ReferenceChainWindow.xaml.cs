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
using AdonisUI.Controls;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
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
        private int _maxDepth = 10;
        private int _progressValue;
        private int _progressMax = 100;
        private string _progressText = "準備中...";
        private readonly IList _selectedItems;
        private double _canvasWidth;
        private double _canvasHeight;
        private double _zoomScale = 1.0;

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
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public ObservableCollection<ReferenceNode> FlatNodes
        {
            get => _flatNodes;
            set { _flatNodes = value; OnPropertyChanged(); }
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
            set { _zoomScale = value; OnPropertyChanged(); }
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

        public ReferenceChainWindow(IList selectedItems)
        {
            InitializeComponent();
            DataContext = this;
            _selectedItems = selectedItems;

            // ウィンドウ表示後に非同期で読み込みを開始
            Loaded += async (s, e) => await LoadReferencesAsync();
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            _ = LoadReferencesAsync();
        }

        private async Task LoadReferencesAsync()
        {
            if (IsLoading) return;
            IsLoading = true;
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
                            // 参照先（Dependencies）のみを構築
                            BuildDependencyTree(rootNode, gameFile, MaxDepth, 0, visited, provider);

                            nodes.Add(rootNode);
                        }
                    }

                    return nodes;
                });

                LayoutNodes(result);
            }
            catch (Exception ex)
            {
                AdonisUI.Controls.MessageBox.Show(this, $"参照の取得中にエラーが発生しました:\n{ex.Message}", "エラー", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void LayoutNodes(List<ReferenceNode> rootNodes)
        {
            var flatList = new List<ReferenceNode>();
            var connectionList = new List<NodeConnection>();
            double currentY = 20;
            double nodeWidth = 250;
            double nodeHeight = 80;
            double horizontalGap = 100;
            double verticalGap = 20;

            foreach (var root in rootNodes)
            {
                CalculatePositions(root, 0, ref currentY, flatList, connectionList, nodeWidth, nodeHeight, horizontalGap, verticalGap);
            }

            FlatNodes = new ObservableCollection<ReferenceNode>(flatList);
            Connections = new ObservableCollection<NodeConnection>(connectionList);

            if (flatList.Any())
            {
                CanvasWidth = flatList.Max(n => n.X) + nodeWidth + 50;
                CanvasHeight = currentY + 50;
            }
        }

        private void CalculatePositions(ReferenceNode node, int depth, ref double currentY, List<ReferenceNode> flatList, List<NodeConnection> connections, double w, double h, double hGap, double vGap)
        {
            node.X = depth * (w + hGap) + 100;

            if (node.Children.Count == 0)
            {
                node.Y = currentY;
                currentY += h + vGap;
            }
            else
            {
                var childYs = new List<double>();
                foreach (var child in node.Children)
                {
                    CalculatePositions(child, depth + 1, ref currentY, flatList, connections, w, h, hGap, vGap);
                    childYs.Add(child.Y);
                    connections.Add(new NodeConnection { Source = node, Target = child, X1 = node.X + w, Y1 = 0, X2 = child.X, Y2 = child.Y + h / 2 });
                }
                node.Y = (childYs.First() + childYs.Last()) / 2;
                // 親ノードのY座標が決まったので、接続線の始点を更新
                foreach (var child in node.Children)
                {
                    var conn = connections.LastOrDefault(c => c.X2 == child.X && c.Y2 == child.Y + h / 2);
                    if (conn != null) conn.Y1 = node.Y + h / 2;
                }
            }

            flatList.Add(node);
        }

        // Zooming
        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return;

            e.Handled = true;
            var scrollViewer = (ScrollViewer)sender;
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
        private void OnScrollViewerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _lastViewMousePosition = e.GetPosition(MainScrollViewer);
            _isDraggingView = true;
            MainScrollViewer.CaptureMouse();
            e.Handled = true;
        }

        private void OnScrollViewerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingView = false;
            MainScrollViewer.ReleaseMouseCapture();
        }

        private void OnScrollViewerMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingView)
            {
                var currentPos = e.GetPosition(MainScrollViewer);
                var delta = currentPos - _lastViewMousePosition;
                MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset - delta.X);
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - delta.Y);
                _lastViewMousePosition = currentPos;
            }
        }

        // Node Dragging
        private void OnNodeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ReferenceNode node)
            {
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
                        conn.X1 = _draggingNode.X + 250; // Node Width
                        conn.Y1 = _draggingNode.Y + 40;  // Node Height / 2
                    }
                    else if (conn.Target == _draggingNode)
                    {
                        conn.X2 = _draggingNode.X;
                        conn.Y2 = _draggingNode.Y + 40;
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
                parentNode.Background = GetBrushForClass(className);

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
                        var childNode = new ReferenceNode { Name = depFile.Name, Path = depFile.Path };
                        parentNode.Children.Add(childNode);
                        BuildDependencyTree(childNode, depFile as GameFile, maxDepth, currentDepth + 1, visited, provider);
                    }
                }
            }
            catch { }
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

        private Brush GetBrushForClass(string className)
        {
            var colorCode = className switch
            {
                "Texture2D" or "TextureCube" or "TextureRenderTarget2D" => "#5D4037", // Brown
                "Material" or "MaterialInstanceConstant" => "#2E7D32", // Green
                "StaticMesh" => "#00838F", // Cyan
                "SkeletalMesh" => "#6A1B9A", // Purple
                "Blueprint" or "BlueprintGeneratedClass" => "#1565C0", // Blue
                "SoundWave" or "SoundCue" => "#EF6C00", // Orange
                "Font" or "FontFace" => "#616161", // Grey
                "World" => "#C62828", // Red
                _ => "#37474F" // Blue Grey (Default)
            };

            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorCode));
                brush.Freeze();
                return brush;
            }
            catch { return Brushes.Transparent; }
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

        private double _x;
        public double X { get => _x; set { _x = value; OnPropertyChanged(); } }

        private double _y;
        public double Y { get => _y; set { _y = value; OnPropertyChanged(); } }

        private static readonly Brush DefaultBackground;

        static ReferenceNode()
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#37474F"));
            brush.Freeze();
            DefaultBackground = brush;
        }

        private Brush _background = DefaultBackground;
        public Brush Background { get => _background; set { _background = value; OnPropertyChanged(); } }

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