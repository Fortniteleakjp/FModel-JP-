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

        if (resourceKey != null && container is FrameworkElement element &&
            element.TryFindResource(resourceKey) is DataTemplate template)
        {
            return template;
        }

        return base.SelectTemplate(item, container);
    }
}
