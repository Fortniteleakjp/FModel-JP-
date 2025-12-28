
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using FModel;
using FModel.Framework;
using FModel.Services;
using CUE4Parse.FileProvider.Objects;
using FModel.Views.Resources.Controls;

namespace FModel.ViewModels
{

    public class ExplorerTabViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ExplorerFileItem> Items { get; set; } = new();

        // 例: "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics/Characters/Character_FinchVisit.uasset"
        public ExplorerTabViewModel(string filePath)
        {
            var assetsFolder = ApplicationService.ApplicationView.CUE4Parse.AssetsFolder;
            if (assetsFolder == null) return;

            // ルートフォルダのみを初期ロードします（遅延読み込みのため）
            foreach (var folder in assetsFolder.Folders)
            {
                Items.Add(new ExplorerFileItem(folder));
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                ExpandToPath(filePath);
            }
        }

        private void ExpandToPath(string path)
        {
            var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var currentItems = Items;

            foreach (var part in parts)
            {
                var item = currentItems.FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (item == null) break;

                item.IsExpanded = true;
                currentItems = item.Children;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ExplorerFileItem : INotifyPropertyChanged
    {
        private bool _isExpanded;
        private bool _wasExpanded;
        private readonly TreeItem _sourceTreeItem;

        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public ObservableCollection<ExplorerFileItem> Children { get; set; } = new();
        public string Icon { get; set; }
        public ICommand OpenCommand { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged(nameof(IsExpanded));
                    if (_isExpanded && !_wasExpanded)
                    {
                        _wasExpanded = true;
                        LoadChildren();
                    }
                }
            }
        }

        public ExplorerFileItem(TreeItem treeItem)
        {
            _sourceTreeItem = treeItem;
            Name = treeItem.Header;
            FullPath = treeItem.PathAtThisPoint;
            IsDirectory = true;
            Icon = "/FModel;component/Resources/folder.png";
            OpenCommand = new RelayCommand(obj => { IsExpanded = !IsExpanded; });

            // 子要素がある場合はダミーを追加して展開可能にする
            if (treeItem.Folders.Count > 0 || treeItem.AssetsList.Assets.Count > 0)
            {
                Children.Add(null);
            }
        }

        public ExplorerFileItem(GameFile gameFile)
        {
            Name = gameFile.Name;
            FullPath = gameFile.Path;
            IsDirectory = false;
            Icon = GetIcon();
            OpenCommand = new RelayCommand(obj => Open());
        }

        private void LoadChildren()
        {
            Children.Clear();
            if (_sourceTreeItem == null) return;

            // フォルダを追加
            foreach (var folder in _sourceTreeItem.Folders)
            {
                Children.Add(new ExplorerFileItem(folder));
            }
            // ファイルを追加
            foreach (var file in _sourceTreeItem.AssetsList.Assets)
            {
                Children.Add(new ExplorerFileItem(file));
            }
        }

        private string GetIcon()
        {
            if (IsDirectory)
                return "/FModel;component/Resources/folder.png";

            var ext = Path.GetExtension(FullPath)?.ToLower().TrimStart('.');
            return ext switch
            {
                "uasset" => "/FModel;component/Resources/asset.png",
                "ini" => "/FModel;component/Resources/asset_ini.png",
                "png" => "/FModel;component/Resources/asset_png.png",
                "psd" => "/FModel;component/Resources/asset_psd.png",
                _ => "/FModel;component/Resources/unknown_asset.png"
            };
        }

        private async void Open()
        {
            if (IsDirectory) return;
            var appView = ApplicationService.ApplicationView;
            if (appView.CUE4Parse.Provider.TryGetGameFile(FullPath, out var gameFile))
            {
                await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
                {
                    appView.CUE4Parse.Extract(cancellationToken, gameFile, true);
                });
            }
            else
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"Could not find file in provider: {FullPath}", Constants.RED, true));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

}
