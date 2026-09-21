using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services;
using FModel.Services.Athena;
using FModel.Views.Resources.Controls;

namespace FModel.ViewModels;

public class AthenaQueueManagerViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;

    private GameFile _selectedItem;
    public GameFile SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            RaisePropertyChanged(nameof(HasSelection));
        }
    }

    public ObservableCollection<GameFile> Items { get; }
    public bool HasItems => Items.Count > 0;
    public bool HasSelection => SelectedItem != null;

    public AthenaQueueManagerViewModel()
    {
        Items = new ObservableCollection<GameFile>(AthenaExportQueue.Snapshot());
        SelectedItem = Items.FirstOrDefault();
    }

    public void RemoveSelected()
    {
        var selected = SelectedItem;
        if (selected is null) return;

        if (AthenaExportQueue.Remove(selected))
            Items.Remove(selected);

        SelectedItem = Items.FirstOrDefault();
        RaisePropertyChanged(nameof(HasItems));
    }

    public void Clear()
    {
        if (!HasItems) return;

        AthenaExportQueue.Clear();
        Items.Clear();
        SelectedItem = null;
        RaisePropertyChanged(nameof(HasItems));
    }

    public async Task GenerateProfile()
    {
        var queue = AthenaExportQueue.Snapshot();
        if (queue.Length == 0)
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("The athena queue is empty, add cosmetics to it first", Constants.WHITE, true));
            return;
        }

        await _threadWorkerView.Begin(cancellationToken =>
            AthenaProfileGenerator.Generate(queue, _applicationView.CUE4Parse.Provider, cancellationToken));
    }
}
