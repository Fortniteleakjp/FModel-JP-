using System.Windows;
using System.Windows.Controls;

namespace FModel.Views.Resources.Controls;

public sealed class TreeViewItemBehavior
{
    public static bool GetIsBroughtIntoViewWhenSelected(TreeViewItem treeViewItem)
    {
        return (bool) treeViewItem.GetValue(IsBroughtIntoViewWhenSelectedProperty);
    }

    public static void SetIsBroughtIntoViewWhenSelected(TreeViewItem treeViewItem, bool value)
    {
        treeViewItem.SetValue(IsBroughtIntoViewWhenSelectedProperty, value);
    }

    public static readonly DependencyProperty IsBroughtIntoViewWhenSelectedProperty =
        DependencyProperty.RegisterAttached("IsBroughtIntoViewWhenSelected", typeof(bool), typeof(TreeViewItemBehavior),
            new UIPropertyMetadata(false, OnIsBroughtIntoViewWhenSelectedChanged));

    private static void OnIsBroughtIntoViewWhenSelectedChanged(DependencyObject depObj, DependencyPropertyChangedEventArgs e)
    {
        if (depObj is not TreeViewItem item)
            return;

        if (e.NewValue is not bool value)
            return;

        if (value)
            item.Selected += OnTreeViewItemSelected;
        else
            item.Selected -= OnTreeViewItemSelected;
    }

    private static void OnTreeViewItemSelected(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem item)
            BringHeaderIntoView(item);
    }

    /// <summary>
    /// Brings only the item's own row into view. The item's bounds include its expanded
    /// children, so for a large expanded folder a plain BringIntoView scrolls to somewhere
    /// inside that subtree and the selected row itself stays off-screen.
    /// </summary>
    public static void BringHeaderIntoView(TreeViewItem item)
    {
        item.ApplyTemplate();
        if (item.Template?.FindName("PART_Header", item) is FrameworkElement header)
            header.BringIntoView();
        else
            item.BringIntoView();
    }
}