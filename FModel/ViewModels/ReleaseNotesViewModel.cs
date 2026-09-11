using System.Linq;
using System.Threading.Tasks;
using FModel.Framework;
using FModel.Services.ReleaseNotes;

namespace FModel.ViewModels;

public class ReleaseNotesViewModel : ViewModel
{
    public RangeObservableCollection<ReleaseNote> Releases { get; } = [];

    private ReleaseNote _selectedRelease;
    public ReleaseNote SelectedRelease
    {
        get => _selectedRelease;
        set => SetProperty(ref _selectedRelease, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private bool _isEmpty;
    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>
    /// まず同梱分を即座に表示し、そのあと GitHub Releases で補完する。
    /// </summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            Releases.AddRange(ReleaseNotesService.GetBundled());
            SelectedRelease = Releases.FirstOrDefault();

            var all = await ReleaseNotesService.LoadAsync();
            if (all.Length != Releases.Count)
            {
                var selectedKey = SelectedRelease?.Key;
                Releases.Clear();
                Releases.AddRange(all);
                SelectedRelease = Releases.FirstOrDefault(r => r.Key == selectedKey) ?? Releases.FirstOrDefault();
            }

            IsEmpty = Releases.Count == 0;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
