
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace FModel.ViewModels
{

    public class ExplorerTabViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ExplorerFileItem> Items { get; set; } = new();

        // 例: "FortniteGame/Plugins/GameFeatures/BRCosmetics/Content/Athena/Items/Cosmetics/Characters/Character_FinchVisit.uasset"
        public ExplorerTabViewModel(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            // ルートディレクトリ（最上位フォルダ）を抽出
            var root = GetRootDirectory(filePath);
            if (root == null)
                return;

            // ルートからツリーを構築
            var rootFullPath = FindFullPath(root, filePath);
            if (rootFullPath != null && Directory.Exists(rootFullPath))
            {
                var rootItem = new ExplorerFileItem(rootFullPath, true);
                Items.Add(rootItem);
                // ルート配下を再帰的に追加
                AddChildrenRecursive(rootItem);
            }

        }

        private void AddChildrenRecursive(ExplorerFileItem parent)
        {
            if (!parent.IsDirectory) return;
            try
            {
                foreach (var dir in Directory.GetDirectories(parent.FullPath))
                {
                    var child = new ExplorerFileItem(dir, true);
                    parent.Children.Add(child);
                    AddChildrenRecursive(child);
                }
                foreach (var file in Directory.GetFiles(parent.FullPath))
                {
                    parent.Children.Add(new ExplorerFileItem(file, false));
                }
            }
            catch { }
        }

        private string GetRootDirectory(string path)
        {
            var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        // filePath内のrootDirに該当する実際のフルパスを探す
        private string FindFullPath(string rootDir, string filePath)
        {
            // ワークスペース内を検索
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable);
            foreach (var drive in drives)
            {
                try
                {
                    var dirs = Directory.GetDirectories(drive.RootDirectory.FullName, rootDir, SearchOption.AllDirectories);
                    if (dirs != null && dirs.Length > 0)
                        return dirs[0];
                }
                catch { }
            }
            return null;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ExplorerFileItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool IsDirectory { get; set; }
        public ObservableCollection<ExplorerFileItem> Children { get; set; } = new();

        public ExplorerFileItem(string path, bool isDirectory)
        {
            Name = Path.GetFileName(path);
            FullPath = path;
            IsDirectory = isDirectory;
            if (isDirectory)
            {
                try
                {
                    foreach (var dir in Directory.GetDirectories(path))
                        Children.Add(new ExplorerFileItem(dir, true));
                    foreach (var file in Directory.GetFiles(path))
                        Children.Add(new ExplorerFileItem(file, false));
                }
                catch { }
            }
        }
    }
}
