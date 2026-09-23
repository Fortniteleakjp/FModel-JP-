using System.Collections.Generic;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.Services;
using FModel.Views.Resources.Controls;
using Serilog;

namespace FModel.ViewModels.Commands;

public class GoToCommand : ViewModelCommand<CustomDirectoriesViewModel>
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    public GoToCommand(CustomDirectoriesViewModel contextViewModel) : base(contextViewModel)
    {
    }

    public override async void Execute(CustomDirectoriesViewModel contextViewModel, object parameter)
    {
        if (parameter is not string s || string.IsNullOrEmpty(s)) return;

        await JumpToAsync(s);
    }

    public async Task<TreeItem> JumpToAsync(string directory)
    {
        _applicationView.SelectedLeftTabIndex = 1; // folders tab
        if (!_applicationView.CUE4Parse.AssetsFolder.TryGetFolder(directory, out var folder))
        {
            Log.Warning("Go To: folder {Directory} was not found", directory);
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"Folder '{directory}' was not found in the loaded archives", Constants.WHITE, true));
            return null;
        }

        // Always walk the realized containers. Setting IsSelected on the model alone does nothing
        // while the container is virtualized, and the selection then fires later, whenever WPF
        // happens to realize it (the "sudden jump" after an unrelated action).
        var ancestors = new Stack<TreeItem>();
        for (var ancestor = folder; ancestor != null; ancestor = ancestor.Parent)
            ancestors.Push(ancestor);

        var path = new List<TreeItem>(ancestors.Count);
        while (ancestors.TryPop(out var ancestor))
            path.Add(ancestor);

        if (await MainWindow.YesWeCats.SelectFolderAsync(path))
            return folder;

        Log.Warning("Go To: could not select {Directory} (cancelled or timed out)", directory);
        return null;
    }
}
