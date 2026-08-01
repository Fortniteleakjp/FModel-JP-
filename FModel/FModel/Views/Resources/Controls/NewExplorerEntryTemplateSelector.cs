using System.Windows;
using System.Windows.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.ViewModels;

namespace FModel.Views.Resources.Controls;

public sealed class NewExplorerEntryTemplateSelector : DataTemplateSelector
{
    public override DataTemplate SelectTemplate(object item, DependencyObject container)
    {
        var resourceKey = item switch
        {
            TreeItem => "NewExplorerFolderTemplate",
            GameFile => "NewExplorerFileTemplate",
            _ => null
        };

        if (resourceKey != null && container is FrameworkElement element)
        {
            // During item generation the ListBoxItem may not yet be connected to
            // the visual tree, so TryFindResource alone can miss ListBox.Resources.
            if (element.TryFindResource(resourceKey) is DataTemplate template)
                return template;

            if (element is ItemsControl itemsControl && itemsControl.Resources[resourceKey] is DataTemplate localTemplate)
                return localTemplate;

            if (ItemsControl.ItemsControlFromItemContainer(element) is ItemsControl owner &&
                owner.Resources[resourceKey] is DataTemplate ownerTemplate)
                return ownerTemplate;
        }

        return base.SelectTemplate(item, container);
    }
}
