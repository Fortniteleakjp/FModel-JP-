using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using FModel.Framework;
using FModel.Settings;

namespace FModel.ViewModels.FolderBrowser
{
    public class FolderBrowserViewModel : ViewModel
    {
        private string? _currentPath;
        public string CurrentPath
        {
            get => _currentPath;
            set
            {
                if (SetProperty(ref _currentPath, value))
                {
                    LoadDirectoryContents();
                }
            }
        }

        public ObservableCollection<FileSystemItem> Items { get; } = new();

        public ICommand NavigateUpCommand { get; }
        public ICommand NavigateToItemCommand { get; }

        public FolderBrowserViewModel()
        {
            NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => CanNavigateUp());
            NavigateToItemCommand = new RelayCommand(item => NavigateToItem(item as FileSystemItem));
            CurrentPath = "D:\\"; // 初期パスの例
        }

        private void LoadDirectoryContents()
        {
            Items.Clear();
            try
            {   if (string.IsNullOrEmpty(CurrentPath)) return;
                if (Directory.Exists(CurrentPath))
                {
                    // ディレクトリを追加
                    foreach (var dir in Directory.GetDirectories(CurrentPath))
                    {
                        Items.Add(new FileSystemItem(dir, true));
                    }
                    // ファイルを追加
                    foreach (var file in Directory.GetFiles(CurrentPath))
                    {
                        Items.Add(new FileSystemItem(file, false));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // アクセス権がないフォルダの場合の処理
            }
            // 他の例外処理もここに追加できます
        }

        private void NavigateToItem(FileSystemItem item)
        {
            if (item is { IsDirectory: true })
            {
                CurrentPath = item.FullPath;
            }
        }

        private void NavigateUp()
        {
            if (string.IsNullOrEmpty(CurrentPath)) return;
            var parent = Directory.GetParent(CurrentPath);
            if (parent != null)
            {
                CurrentPath = parent.FullName;
            }
        }

        private bool CanNavigateUp() => !string.IsNullOrEmpty(CurrentPath) && Directory.GetParent(CurrentPath) != null;
    }
}