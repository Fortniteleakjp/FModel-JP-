using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FModel.Services;
using FModel.ViewModels;

namespace FModel.Views.Resources.Controls.TiledExplorer;

public partial class ResourcesDictionary
{
    public ResourcesDictionary()
    {
        InitializeComponent();
    }

    private void OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;

        switch (item.DataContext)
        {
            case GameFileViewModel file:
                ApplicationService.ApplicationView.SelectedLeftTabIndex = 2;
                file.IsSelected = true;
                file.ExtractAsync();
                break;
            case TreeItem folder:
                ApplicationService.ApplicationView.SelectedLeftTabIndex = 1;

                // Expand all parent folders if not expanded
                var parent = folder.Parent;
                while (parent != null)
                {
                    parent.IsExpanded = true;
                    parent = parent.Parent;
                }

                // Auto expand single child folders
                var childFolder = folder;
                while (childFolder.Folders.Count == 1 && childFolder.AssetsList.Count == 0)
                {
                    childFolder.IsExpanded = true;
                    childFolder = childFolder.Folders[0];
                }

                childFolder.IsExpanded = true;
                childFolder.IsSelected = true;
                break;
        }
    }

    // Hack to force re-evaluation of context menu options, also prevents menu flicker from happening
    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item)
            return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is not ListBox listBox)
            return;

        if (item.DataContext is TreeItem)
        {
            // フォルダは DataTemplate 側の FolderContextMenu が開くので、選択だけ合わせて任せる。
            // 複数選択済みのフォルダを右クリックした場合はその選択を維持する。
            if (!item.IsSelected)
            {
                listBox.UnselectAll();
                item.IsSelected = true;
            }
            item.Focus();
            return;
        }

        if (item.DataContext is not GameFileViewModel)
            return;

        if (!item.IsSelected)
        {
            listBox.UnselectAll();
            item.IsSelected = true;
        }
        item.Focus();

        var contextMenu = listBox.FindResource("FileContextMenu") as ContextMenu;
        if (contextMenu is not null)
        {
            listBox.ContextMenu = null;
            item.Dispatcher.BeginInvoke(new Action(() =>
            {
                contextMenu.DataContext = listBox.DataContext;
                listBox.ContextMenu = contextMenu;
                contextMenu.PlacementTarget = listBox;
                contextMenu.IsOpen = true;
            }), DispatcherPriority.Input);
        }

        e.Handled = true;
    }
}
