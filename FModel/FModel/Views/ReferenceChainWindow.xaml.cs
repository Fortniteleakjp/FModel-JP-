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
            node.X = depth * (w + hGap) + 20;

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
                    connections.Add(new NodeConnection { X1 = node.X + w, Y1 = 0, X2 = child.X, Y2 = child.Y + h / 2 });
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

                var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // パッケージからインポート（依存先）を取得
                if (ipackage is IoPackage ioPackage)
                {
                    foreach (var importIndex in ioPackage.ImportMap)
                    {
                        var resolved = ioPackage.ResolveObjectIndex(importIndex);
                        if (resolved?.Class?.Name.Text == "Package") dependencies.Add(resolved.Name.Text);
                    }
                }
                else if (ipackage is Package package)
                {
                    foreach (var import in package.ImportMap)
                    {
                        if (import.ClassName.Text == "Package") dependencies.Add(import.ObjectName.Text);
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class NodeConnection
    {
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }
}