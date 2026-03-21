using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FModel.Services;
using FModel.ViewModels;
using FModel.Views;

namespace FModel.Views
{
    public partial class ExplorerTab : UserControl
    {
        public ExplorerTab(string rootPath = null)
        {
            InitializeComponent();
            this.DataContext = new ExplorerTabViewModel(rootPath);
        }

        private void OnOpenReferenceViewerClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                var appView = ApplicationService.ApplicationView;
                if (appView.CUE4Parse.Provider.TryGetGameFile(item.FullPath, out var gameFile))
                {
                    var assets = new List<CUE4Parse.FileProvider.Objects.GameFile> { gameFile };
                    var window = new ReferenceChainWindow(assets);
                    window.Show();
                }
            }
        }

        private void OnSaveAsClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                var appView = ApplicationService.ApplicationView;
                if (appView.CUE4Parse.Provider.TryGetGameFile(item.FullPath, out var gameFile))
                {
                    var selectedItems = new List<CUE4Parse.FileProvider.Objects.GameFile> { gameFile };
                    if (appView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Export_Data", selectedItems }))
                    {
                        appView.RightClickMenuCommand.Execute(new object[] { "Assets_Export_Data", selectedItems });
                    }
                }
            }
        }

        private void OnSaveJsonClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                var appView = ApplicationService.ApplicationView;
                if (appView.CUE4Parse.Provider.TryGetGameFile(item.FullPath, out var gameFile))
                {
                    var selectedItems = new List<CUE4Parse.FileProvider.Objects.GameFile> { gameFile };
                    if (appView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Save_Properties", selectedItems }))
                    {
                        appView.RightClickMenuCommand.Execute(new object[] { "Assets_Save_Properties", selectedItems });
                    }
                }
            }
        }

        private void OnSaveTextureClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                var appView = ApplicationService.ApplicationView;
                if (appView.CUE4Parse.Provider.TryGetGameFile(item.FullPath, out var gameFile))
                {
                    var selectedItems = new List<CUE4Parse.FileProvider.Objects.GameFile> { gameFile };
                    if (appView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Save_Textures", selectedItems }))
                    {
                        appView.RightClickMenuCommand.Execute(new object[] { "Assets_Save_Textures", selectedItems });
                    }
                }
            }
        }

        private void OnSaveModelClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                var appView = ApplicationService.ApplicationView;
                if (appView.CUE4Parse.Provider.TryGetGameFile(item.FullPath, out var gameFile))
                {
                    var selectedItems = new List<CUE4Parse.FileProvider.Objects.GameFile> { gameFile };
                    if (appView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Save_Models", selectedItems }))
                    {
                        appView.RightClickMenuCommand.Execute(new object[] { "Assets_Save_Models", selectedItems });
                    }
                }
            }
        }

        private void OnSaveAnimClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                var appView = ApplicationService.ApplicationView;
                if (appView.CUE4Parse.Provider.TryGetGameFile(item.FullPath, out var gameFile))
                {
                    var selectedItems = new List<CUE4Parse.FileProvider.Objects.GameFile> { gameFile };
                    if (appView.RightClickMenuCommand.CanExecute(new object[] { "Assets_Save_Animations", selectedItems }))
                    {
                        appView.RightClickMenuCommand.Execute(new object[] { "Assets_Save_Animations", selectedItems });
                    }
                }
            }
        }

        private void OnAddToCartClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                MainWindow.YesWeCats?.AddAssetPathToExportCart(item.FullPath);
            }
        }

        private void OnRemoveFromCartClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item && !item.IsDirectory)
            {
                MainWindow.YesWeCats?.RemoveAssetPathFromExportCart(item.FullPath);
            }
        }

        private void OnCopyPathClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item)
            {
                Clipboard.SetText(item.FullPath);
            }
        }

        private void OnCopyNameClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item)
            {
                Clipboard.SetText(item.Name);
            }
        }

        private void OnCopyFolderPathClick(object sender, RoutedEventArgs e)
        {
            if (ExplorerTreeView.SelectedItem is ExplorerFileItem item)
            {
                var folderPath = item.FullPath;
                var lastSlash = folderPath.LastIndexOf('/');
                if (lastSlash > 0)
                {
                    Clipboard.SetText(folderPath.Substring(0, lastSlash));
                }
                else
                {
                    Clipboard.SetText(folderPath);
                }
            }
        }
    }
}