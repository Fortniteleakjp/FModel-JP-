using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FModel.Extensions;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;

namespace FModel.Views.Resources.Controls.ContextMenus;

public partial class FolderContextMenuDictionary
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public FolderContextMenuDictionary()
    {
        InitializeComponent();
    }

    private void FolderContextMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement fe } menu)
            return;

        var listBox = fe.FindAncestor<ListBox>();
        if (listBox != null)
        {
            menu.DataContext = listBox.DataContext;

            // 複数選択されていればそれを全部使う。選択外のフォルダを右クリックした場合は、
            // エクスプローラーと同じようにそのフォルダだけを対象にする。
            var selection = listBox.SelectedItems.OfType<object>().ToList();
            if (fe.DataContext is TreeItem clicked && !selection.Contains(clicked))
                selection = [clicked];

            menu.Tag = selection;
            return;
        }

        var treeView = fe.FindAncestor<TreeView>();
        if (treeView != null)
        {
            menu.DataContext = treeView.DataContext;

            // ツリーは単一選択。右クリックしただけでは選択が移らないので、
            // 選択中のフォルダではなく実際に右クリックされたフォルダを対象にする。
            menu.Tag = new List<object> { fe.DataContext as TreeItem ?? treeView.SelectedItem };
        }
    }

    private void OnFavoriteDirectoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: IEnumerable<object> list } || list.FirstOrDefault() is not TreeItem folder)
            return;

        _applicationView.CustomDirectories.Add(new CustomDirectory(folder.Header, folder.PathAtThisPoint));
        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Successfully saved '{folder.PathAtThisPoint}' as a new favorite directory", Constants.WHITE, true));
    }

    private void OnCopyDirectoryPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { CommandParameter: IEnumerable<object> list } || list.FirstOrDefault() is not TreeItem folder)
            return;

        Clipboard.SetText(folder.PathAtThisPoint);
    }
}
