using System;
using System.Linq;
using FModel.Framework;
using FModel.Services;

namespace FModel.ViewModels.Commands;

public class GoToCommand : ViewModelCommand<CustomDirectoriesViewModel>
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public GoToCommand(CustomDirectoriesViewModel contextViewModel) : base(contextViewModel)
    {
    }

    public override void Execute(CustomDirectoriesViewModel contextViewModel, object parameter)
    {
        if (parameter is not string s || string.IsNullOrEmpty(s)) return;

        JumpTo(s);
    }

    public TreeItem JumpTo(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        if (_applicationView.CUE4Parse.AssetsFolder.TryGetFolder(directory, out var indexedFolder))
        {
            MainWindow.YesWeCats.LeftTabControl.SelectedIndex = 1;
            for (var ancestor = indexedFolder; ancestor != null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
            indexedFolder.IsSelected = true;
            return indexedFolder;
        }

        var folders = directory
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (folders.Length == 0)
            return null;

        MainWindow.YesWeCats.LeftTabControl.SelectedIndex = 1; // folders tab
        var root = _applicationView.CUE4Parse.AssetsFolder.Folders;
        if (root is not { Count: > 0 }) return null;

        for (var i = 0; i < folders.Length; i++)
        {
            var next = root.FirstOrDefault(folder =>
                folder.Header.Equals(folders[i], StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;

            next.IsExpanded = true;
            if (i == folders.Length - 1)
            {
                next.IsSelected = true;
                return next;
            }

            root = next.Folders;
            if (root is not { Count: > 0 })
                return null;
        }

        return null;
    }
}
