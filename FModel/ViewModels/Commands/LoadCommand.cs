using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Services.Athena;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Microsoft.Win32;

namespace FModel.ViewModels.Commands;

/// <summary>
/// this will always load all files no matter the loading mode
/// however what this does is filtering what to show to the user
/// </summary>
public class LoadCommand : ViewModelCommand<LoadingModesViewModel>
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private DiscordHandler _discordHandler => DiscordService.DiscordHandler;

    public LoadCommand(LoadingModesViewModel contextViewModel) : base(contextViewModel) { }

    public override async void Execute(LoadingModesViewModel contextViewModel, object parameter)
    {
        if (_applicationView.CUE4Parse.Provider.Keys.Count == 0 && _applicationView.CUE4Parse.Provider.RequiredKeys.Count > 0)
        {
            FLogger.Append(ELog.Error, () =>
                FLogger.Text("An encrypted archive has been found. In order to decrypt it, please specify a working AES encryption key", Constants.WHITE, true));
            return;
        }
        if (_applicationView.CUE4Parse.Provider.Files.Count == 0)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("No files were found in the archives or the specified directory", Constants.WHITE, true));
            return;
        }

#if DEBUG
        var loadingTime = Stopwatch.StartNew();
#endif
        _applicationView.CUE4Parse.AssetsFolder.Clear();
        _applicationView.CUE4Parse.SearchVm.Clear();
        AthenaExportQueue.Clear(); // 古いプロバイダのアセットが残らないように
        _applicationView.SelectedLeftTabIndex = 1; // folders tab
        _applicationView.IsAssetsExplorerVisible = true;
        Helper.CloseWindow<AdonisWindow>("Search For Packages"); // close search window if opened

        // Populate the package tree before doing supplemental reads. For streamed providers those
        // reads may download BuildPatch chunks and must not delay or hide an otherwise valid tree.
        await _threadWorkerView.Begin(cancellationToken =>
        {
            // filter what to show
            _applicationView.Status.UpdateStatusLabel("Packages", "Filtering");
            switch (UserSettings.Default.LoadingMode)
            {
                case ELoadingMode.Multiple:
                {
                    var l = (IList) parameter;
                    if (l.Count == 0)
                    {
                        UserSettings.Default.LoadingMode = ELoadingMode.All;
                        goto case ELoadingMode.All;
                    }

                    var directoryFilesToShow = l.Cast<FileItem>();
                    FilterDirectoryFilesToDisplay(cancellationToken, directoryFilesToShow);
                    break;
                }
                case ELoadingMode.All:
                {
                    FilterDirectoryFilesToDisplay(cancellationToken, null);
                    break;
                }
                case ELoadingMode.AllButNew:
                case ELoadingMode.AllButModified:
                {
                    FilterNewOrModifiedFilesToDisplay(cancellationToken);
                    break;
                }
                case ELoadingMode.AllButPatched:
                {
                    FilterPacthedFilesToDisplay(cancellationToken);
                    break;
                }
                default: throw new ArgumentOutOfRangeException();
            }

            _discordHandler.UpdatePresence(_applicationView.CUE4Parse);
        }).ConfigureAwait(false);

        // These enrich later package reads but are not required to display the archive contents.
        // Run them sequentially to avoid competing BuildPatch download bursts on Fortnite Live.
        await _applicationView.CUE4Parse.LoadVirtualPaths().ConfigureAwait(false);
        await _applicationView.CUE4Parse.LoadLocalizedResources().ConfigureAwait(false);
#if DEBUG
        loadingTime.Stop();
        FLogger.Append(ELog.Debug, () =>
            FLogger.Text($"{_applicationView.CUE4Parse.SearchVm.SearchResults.Count} packages loaded in {loadingTime.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} seconds", Constants.WHITE, true));
#endif
    }

    private void FilterDirectoryFilesToDisplay(CancellationToken cancellationToken, IEnumerable<FileItem> directoryFiles)
    {
        HashSet<string> filter;
        var includeLooseFiles = false;
        if (directoryFiles == null) filter = null;
        else
        {
            filter = [];
            foreach (var directoryFile in directoryFiles)
            {
                if (!directoryFile.IsEnabled) continue;
                if (directoryFile.IsLooseFilesContainer)
                {
                    includeLooseFiles = true;
                    continue;
                }
                filter.Add(directoryFile.Name);
            }
        }

        var hasFilter = filter != null && filter.Count != 0;
        var hasSelection = hasFilter || includeLooseFiles;
        var entries = new List<GameFile>();

        foreach (var asset in _applicationView.CUE4Parse.Provider.Files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested(); // cancel if needed
            if (asset.IsUePackagePayload) continue;

            if (hasSelection)
            {
                if (asset is VfsEntry entry && filter.Contains(entry.Vfs.Name))
                {
                    entries.Add(asset);
                }
                else if (includeLooseFiles && asset is not VfsEntry)
                {
                    entries.Add(asset);
                }
            }
            else
            {
                entries.Add(asset);
            }
        }

        _applicationView.Status.UpdateStatusLabel($"{entries.Count:### ### ###} Packages");
        _applicationView.CUE4Parse.AssetsFolder.BulkPopulate(entries);
    }

    private void FilterNewOrModifiedFilesToDisplay(CancellationToken cancellationToken)
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = "Select a backup file older than your current game version",
            InitialDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Backups"),
            Filter = "FBKP Files (*.fbkp)|*.fbkp|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (!openFileDialog.ShowDialog().GetValueOrDefault()) return;

        FLogger.Append(ELog.Information, () =>
            FLogger.Text($"Backup file older than current game is '{openFileDialog.FileName.SubstringAfterLast("\\")}'", Constants.WHITE, true));

        var mode = UserSettings.Default.LoadingMode;
        var entries = ParseBackup(openFileDialog.FileName, mode, cancellationToken);

        _applicationView.Status.UpdateStatusLabel($"{entries.Count:### ### ###} Packages");
        _applicationView.CUE4Parse.AssetsFolder.BulkPopulate(entries);
    }

    private List<GameFile> ParseBackup(string path, ELoadingMode mode, CancellationToken cancellationToken = default)
    {
        var backup = BackupFileReader.Read(path, cancellationToken);
        var entries = new List<GameFile>();

        switch (mode)
        {
            case ELoadingMode.AllButNew:
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in backup) paths.Add(entry.Path);

                foreach (var (key, asset) in _applicationView.CUE4Parse.Provider.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (asset.IsUePackagePayload || paths.Contains(key)) continue;

                    entries.Add(asset);
                }

                break;
            }
            case ELoadingMode.AllButModified:
            {
                foreach (var entry in backup)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddEntry(entry.Path, entry.Size, entry.IsEncrypted, entries);
                }
                break;
            }
        }

        return entries;
    }

    private void AddEntry(string path, long uncompressedSize, bool isEncrypted, List<GameFile> entries)
    {
        if (!_applicationView.CUE4Parse.Provider.Files.TryGetValue(path, out var asset) ||
            asset.IsUePackagePayload || asset.Size == uncompressedSize && asset.IsEncrypted == isEncrypted)
            return;

        entries.Add(asset);
    }

    private void FilterPacthedFilesToDisplay(CancellationToken cancellationToken)
    {
        var loaded = new Dictionary<string, GameFile>(_applicationView.CUE4Parse.Provider.PathComparer);

        foreach (var (key, asset) in _applicationView.CUE4Parse.Provider.Files)
        {
            cancellationToken.ThrowIfCancellationRequested(); // cancel if needed
            if (asset.IsUePackagePayload) continue;

            if (asset is VfsEntry entry && loaded.TryGetValue(key, out var file) &&
                file is VfsEntry existingEntry && entry.Vfs.ReadOrder < existingEntry.Vfs.ReadOrder)
            {
                continue;
            }

            loaded[key] = asset;
        }

        _applicationView.Status.UpdateStatusLabel($"{loaded.Count:### ### ###} Packages");
        _applicationView.CUE4Parse.AssetsFolder.BulkPopulate(loaded.Values);
    }
}
