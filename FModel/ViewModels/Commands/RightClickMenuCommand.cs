using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.Utils;
using FModel.Framework;
using FModel.Services;
using FModel.Services.Athena;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using Ookii.Dialogs.Wpf;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.ViewModels.Commands;

public class RightClickMenuCommand : ViewModelCommand<ApplicationViewModel>
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;

    public RightClickMenuCommand(ApplicationViewModel contextViewModel) : base(contextViewModel) { }

    private enum EAction
    {
        Show,
        Export,
    }

    /// <summary>
    /// Asks for the build directory the current one is compared against.
    /// </summary>
    private static bool TryBrowseDiffDirectory(out string path)
    {
        var selected = string.Empty;
        var picked = System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var folderBrowser = new VistaFolderBrowserDialog
            {
                ShowNewFolderButton = false,
                Description = "Select the build directory to compare the current one against",
                UseDescriptionForTitle = true
            };
            if (folderBrowser.ShowDialog() != true) return false;

            selected = folderBrowser.SelectedPath;
            return true;
        });

        path = picked ? selected : string.Empty;
        return picked;
    }

    private enum EShowAssetType
    {
        None,
        JSON,
        Metadata,
        References,
        ReferenceViewer,
        Decompile,
        VerseDeclarations,
        VerseListing,
        TableViewer,
        WorldOutliner,
        MaterialGraph,
        BlueprintGraph,
        AudioReverseLookup,
        MemberUsage,
        Diff,
        DiffPickFolder,
        DiffPreviousVersion,
        AthenaProfile,
        AthenaQueueAdd,
        AthenaQueueRemove,
    }

    public override async void Execute(ApplicationViewModel contextViewModel, object parameter)
    {
        if (parameter is not object[] parameters || parameters[0] is not string trigger)
            return;

        // キュー操作は選択内容に依存しないので、選択を解決する前に処理する
        if (await TryExecuteAthenaQueueAction(contextViewModel, trigger))
            return;

        var param = (parameters.Length > 1 ? parameters[1] as IEnumerable : null)?.OfType<object>().ToArray() ?? [];
        if (param.Length == 0) return;

        var folders = param.OfType<TreeItem>().ToArray();
        var assets = param
            .Select(static item => item switch
            {
                GameFile gf => gf, // Search view passes GameFile directly
                GameFileViewModel gvm => gvm.Asset,
                _ => null
            })
            .Where(static gf => gf is not null).ToArray();

        if (folders.Length == 0 && assets.Length == 0)
            return;

        var assetsGroups = assets.GroupBy(static gf => gf.Directory);
        var (action, showtype, bulktype) = trigger switch
        {
            "Assets_Extract_New_Tab" => (EAction.Show, EShowAssetType.JSON, EBulkType.None),
            "Assets_Show_Metadata" => (EAction.Show, EShowAssetType.Metadata, EBulkType.None),
            "Assets_Show_References" => (EAction.Show, EShowAssetType.References, EBulkType.None),
            "Assets_Reference_Viewer" => (EAction.Show, EShowAssetType.ReferenceViewer, EBulkType.None),
            "Assets_Diff" => (EAction.Show, EShowAssetType.Diff, EBulkType.None),
            "Assets_Diff_Pick_Folder" => (EAction.Show, EShowAssetType.DiffPickFolder, EBulkType.None),
            "Assets_Diff_Previous_Version" => (EAction.Show, EShowAssetType.DiffPreviousVersion, EBulkType.None),
            "Assets_Decompile" => (EAction.Show, EShowAssetType.Decompile, EBulkType.Code),
            "Assets_Verse_Declarations" => (EAction.Show, EShowAssetType.VerseDeclarations, EBulkType.None),
            "Assets_Verse_Listing" => (EAction.Show, EShowAssetType.VerseListing, EBulkType.None),
            "Assets_Table_Viewer" => (EAction.Show, EShowAssetType.TableViewer, EBulkType.None),
            "Assets_World_Outliner" => (EAction.Show, EShowAssetType.WorldOutliner, EBulkType.None),
            "Assets_Material_Graph" => (EAction.Show, EShowAssetType.MaterialGraph, EBulkType.None),
            "Assets_Blueprint_Graph" => (EAction.Show, EShowAssetType.BlueprintGraph, EBulkType.None),
            "Assets_Audio_Reverse_Lookup" => (EAction.Show, EShowAssetType.AudioReverseLookup, EBulkType.None),
            "Assets_Member_Usage" => (EAction.Show, EShowAssetType.MemberUsage, EBulkType.None),
            "Assets_Athena_Profile" => (EAction.Show, EShowAssetType.AthenaProfile, EBulkType.None),
            "Assets_Athena_Queue_Add" => (EAction.Show, EShowAssetType.AthenaQueueAdd, EBulkType.None),
            "Assets_Athena_Queue_Remove" => (EAction.Show, EShowAssetType.AthenaQueueRemove, EBulkType.None),

            "Save_Data" => (EAction.Export, EShowAssetType.None, EBulkType.Raw),
            "Save_Properties" => (EAction.Export, EShowAssetType.None, EBulkType.Properties),
            "Save_Textures" => (EAction.Export, EShowAssetType.None, EBulkType.Textures),
            "Save_Models" => (EAction.Export, EShowAssetType.None, EBulkType.Meshes),
            "Save_Worlds" => (EAction.Export, EShowAssetType.None, EBulkType.Worlds),
            "Save_Animations" => (EAction.Export, EShowAssetType.None, EBulkType.Animations),
            "Save_Audio" => (EAction.Export, EShowAssetType.None, EBulkType.Audio),
            "Save_Code" => (EAction.Export, EShowAssetType.None, EBulkType.Code),

            _ => throw new ArgumentOutOfRangeException("Unsupported asset action."),
        };

        Interlocked.Exchange(ref contextViewModel.CUE4Parse.ExportedCount, 0);
        Interlocked.Exchange(ref contextViewModel.CUE4Parse.FailedExportCount, 0);
        await _threadWorkerView.Begin(cancellationToken =>
        {
            if (action is EAction.Show)
            {
                if (showtype is EShowAssetType.ReferenceViewer)
                {
                    var roots = assets.ToList();
                    System.Windows.Application.Current.Dispatcher.Invoke(() => new ReferenceChainWindow(roots).Show());
                    return;
                }

                if (showtype is EShowAssetType.TableViewer)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    var documents = TableViewerWindow.Load(entry, null, cancellationToken);
                    if (documents is null)
                    {
                        FLogger.Append(ELog.Warning, () =>
                            FLogger.Text($"{entry.Name} does not contain any data table or curve table", Constants.WHITE, true));
                        return;
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() => new TableViewerWindow(documents, entry.NameWithoutExtension).Show());
                    return;
                }

                if (showtype is EShowAssetType.WorldOutliner)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    var outline = WorldOutlinerWindow.Load(entry, null, cancellationToken);
                    if (outline is null)
                    {
                        FLogger.Append(ELog.Warning, () =>
                            FLogger.Text($"{entry.Name} is not a level", Constants.WHITE, true));
                        return;
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() => new WorldOutlinerWindow(outline).Show());
                    return;
                }

                if (showtype is EShowAssetType.MaterialGraph)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    var graph = MaterialGraphWindow.Load(entry, cancellationToken);
                    if (graph is null)
                    {
                        FLogger.Append(ELog.Warning, () =>
                            FLogger.Text($"{entry.Name} is not a material", Constants.WHITE, true));
                        return;
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() => new MaterialGraphWindow(graph).Show());
                    return;
                }

                if (showtype is EShowAssetType.BlueprintGraph)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    var graph = BlueprintGraphWindow.Load(entry, cancellationToken);
                    if (graph is null)
                    {
                        FLogger.Append(ELog.Warning, () =>
                            FLogger.Text($"{entry.Name} is not a blueprint", Constants.WHITE, true));
                        return;
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() => new BlueprintGraphWindow(graph).Show());
                    return;
                }

                if (showtype is EShowAssetType.AudioReverseLookup)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    AudioReverseLookupWindow.RunAndShow(entry, null, cancellationToken);
                    return;
                }

                if (showtype is EShowAssetType.MemberUsage)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    var (owner, members) = MemberUsageWindow.ReadMembers(entry);
                    if (members is null)
                    {
                        FLogger.Append(ELog.Warning, () =>
                            FLogger.Text($"{entry.Name} is not a blueprint", Constants.WHITE, true));
                        return;
                    }

                    System.Windows.Application.Current.Dispatcher.Invoke(() => new MemberUsageWindow(members: members, owner: owner).Show());
                    return;
                }

                if (showtype is EShowAssetType.AthenaProfile or EShowAssetType.AthenaQueueAdd or EShowAssetType.AthenaQueueRemove)
                {
                    var cosmetics = CollectAthenaCosmetics(assets, folders, cancellationToken);
                    switch (showtype)
                    {
                        case EShowAssetType.AthenaProfile:
                            AthenaProfileGenerator.Generate(cosmetics, contextViewModel.CUE4Parse.Provider, cancellationToken,
                                contextViewModel.ReportAthenaProfileProgress);
                            break;
                        case EShowAssetType.AthenaQueueAdd:
                            var added = AthenaExportQueue.Add(cosmetics, out var duplicateCount);
                            var queued = AthenaExportQueue.Count;
                            FLogger.Append(added > 0 ? ELog.Information : ELog.Warning, () =>
                                FLogger.Text(added > 0
                                    ? $"Added {added} cosmetics to the athena queue ({queued} queued)"
                                    : $"No new cosmetics to add to the athena queue ({queued} queued)", Constants.WHITE, true));

                            if (duplicateCount > 0)
                            {
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    var title = System.Windows.Application.Current.TryFindResource("UI_AthenaQueueDuplicateTitle") as string
                                                ?? "Already in Athena Queue";
                                    var messageTemplate = System.Windows.Application.Current.TryFindResource("UI_AthenaQueueDuplicateMessage") as string
                                                          ?? "{0} selected cosmetics are already in the Athena queue and were not added.";
                                    MessageBox.Show(string.Format(messageTemplate, duplicateCount), title,
                                        MessageBoxButton.OK, MessageBoxImage.Warning);
                                });
                            }
                            break;
                        case EShowAssetType.AthenaQueueRemove:
                            var removed = AthenaExportQueue.Remove(cosmetics);
                            var remaining = AthenaExportQueue.Count;
                            FLogger.Append(removed > 0 ? ELog.Information : ELog.Warning, () =>
                                FLogger.Text(removed > 0
                                    ? $"Removed {removed} cosmetics from the athena queue ({remaining} queued)"
                                    : $"None of the selected cosmetics are in the athena queue ({remaining} queued)", Constants.WHITE, true));
                            break;
                    }

                    return;
                }

                if (showtype is EShowAssetType.DiffPreviousVersion)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    contextViewModel.CUE4Parse.ShowApiAssetDiff(entry.Path, cancellationToken).GetAwaiter().GetResult();
                    return;
                }

                if (showtype is EShowAssetType.Diff or EShowAssetType.DiffPickFolder)
                {
                    var entry = assets.FirstOrDefault();
                    if (entry is null) return;

                    var cue4Parse = contextViewModel.CUE4Parse;
                    if (showtype is EShowAssetType.DiffPickFolder || !cue4Parse.HasDiffProvider)
                    {
                        if (!TryBrowseDiffDirectory(out var directory)) return;
                        cue4Parse.LoadDiffProvider(directory);
                    }

                    cue4Parse.ShowAssetDiff(entry.Path).GetAwaiter().GetResult();
                    return;
                }

                if (showtype is EShowAssetType.References)
                    assets = [assets.FirstOrDefault()];

                Action<GameFile> entryAction = showtype switch
                {
                    EShowAssetType.JSON => entry => contextViewModel.CUE4Parse.Extract(cancellationToken, entry, true),
                    EShowAssetType.Metadata => entry => contextViewModel.CUE4Parse.ShowMetadata(entry),
                    EShowAssetType.Decompile => entry => contextViewModel.CUE4Parse.Decompile(entry),
                    EShowAssetType.VerseDeclarations => entry => contextViewModel.CUE4Parse.RecoverVerseDeclarations(entry),
                    EShowAssetType.VerseListing => entry => contextViewModel.CUE4Parse.RecoverVerseDeclarations(entry, listing: true),
                    EShowAssetType.References => entry => contextViewModel.CUE4Parse.FindReferences(entry),
                    _ => throw new ArgumentOutOfRangeException("Unsupported asset action type."),
                };

                foreach (var entry in assets)
                {
                    Thread.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    entryAction(entry);
                }

                return;
            }

            var (dirType, filetype) = bulktype switch
            {
                EBulkType.Raw => (UserSettings.Default.RawDataDirectory, "files"),
                EBulkType.Properties => (UserSettings.Default.PropertiesDirectory, "json files"),
                EBulkType.Textures => (UserSettings.Default.TextureDirectory, "textures"),
                EBulkType.Meshes => (UserSettings.Default.ModelDirectory, "models"),
                EBulkType.Worlds => (UserSettings.Default.ModelDirectory, "worlds"),
                EBulkType.Animations => (UserSettings.Default.ModelDirectory, "animations"),
                EBulkType.Audio => (UserSettings.Default.AudioDirectory, "audio files"),
                EBulkType.Code => (UserSettings.Default.CodeDirectory, "code files"),
                _ => (null, null),
            };

            if (string.IsNullOrEmpty(dirType))
                return;

            Action<TreeItem> folderAction = bulktype switch
            {
                EBulkType.Raw => folder => contextViewModel.CUE4Parse.ExportFolder(cancellationToken, folder),
                _ => folder => contextViewModel.CUE4Parse.ExtractFolder(cancellationToken, folder, bulktype | EBulkType.Auto),
            };

            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var queuedBefore = ExportSessionViewModel.Instance.Session.TotalQueued;
                folderAction(folder);

                var path = Path.Combine(dirType, UserSettings.Default.KeepDirectoryStructure ? folder.PathAtThisPoint : folder.PathAtThisPoint.SubstringAfterLast('/')).Replace('\\', '/');
                LogExport(contextViewModel, folder.PathAtThisPoint, path, dirType, filetype, queuedBefore);
            }

            Action<GameFile, EBulkType> fileAction = bulktype switch
            {
                EBulkType.Raw => (entry, _) => contextViewModel.CUE4Parse.ExportData(entry),
                _ => (entry, bulk) => contextViewModel.CUE4Parse.Extract(cancellationToken, entry, false, bulk),
            };

            foreach (var group in assetsGroups)
            {
                var directory = group.Key;
                var list = group.ToArray();
                var update = list.Length > 1;
                var bulk = bulktype | (update ? EBulkType.Auto : EBulkType.None);
                var queuedBefore = ExportSessionViewModel.Instance.Session.TotalQueued;
                foreach (var entry in list)
                {
                    Thread.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    fileAction(entry, bulk);
                }

                if (update)
                {
                    var path = Path.Combine(dirType, UserSettings.Default.KeepDirectoryStructure ? directory : directory.SubstringAfterLast('/')).Replace('\\', '/');
                    LogExport(contextViewModel, directory, path, dirType, filetype, queuedBefore);
                }
            }
        });

        if (action is EAction.Export)
        {
            await ExportSessionViewModel.Instance.ExportAutomaticallyAsync();
        }
    }

    /// <summary>
    /// 明示的に選ばれたアセットと、選ばれたフォルダ(直下 + サブフォルダ)のコスメティクスをまとめて集める。
    /// 親子フォルダを同時に選んでも同じアセットは 1 回しか入らない。
    /// </summary>
    private static List<GameFile> CollectAthenaCosmetics(IEnumerable<GameFile> assets, IEnumerable<TreeItem> folders, CancellationToken cancellationToken)
    {
        var cosmetics = new List<GameFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            if (asset is null || !seen.Add(asset.Path)) continue;
            cosmetics.Add(asset);
        }

        foreach (var folder in folders)
            CollectFolderCosmetics(folder, cosmetics, seen, cancellationToken);

        return cosmetics;
    }

    /// <summary>
    /// フォルダ直下のパッケージを拾ったうえで、サブフォルダも再帰的に辿り、
    /// 名前からコスメティクスと判断できるものだけを集める。
    /// </summary>
    private static void CollectFolderCosmetics(TreeItem folder, List<GameFile> cosmetics, HashSet<string> seen, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // AssetsList.Assets はビューモデルを生成してしまうため、ワーカースレッドからは生の GameFile を見る
        foreach (var asset in folder.AssetsList.RawAssets)
        {
            if (asset is null || !asset.IsUePackage) continue;
            if (!AthenaItemTable.IsCosmeticName(asset.NameWithoutExtension)) continue;
            if (!seen.Add(asset.Path)) continue;

            cosmetics.Add(asset);
        }

        foreach (var sub in folder.Folders)
            CollectFolderCosmetics(sub, cosmetics, seen, cancellationToken);
    }

    /// <summary>
    /// キューに溜めたコスメティクスをまとめて出力する系のトリガーを処理する。
    /// 処理したときだけ true を返す。
    /// </summary>
    private async Task<bool> TryExecuteAthenaQueueAction(ApplicationViewModel contextViewModel, string trigger)
    {
        switch (trigger)
        {
            case "Assets_Athena_Queue_Manage":
            {
                new AthenaQueueManagerWindow().ShowDialog();
                return true;
            }
            case "Assets_Athena_Queue_Clear":
            {
                var queued = AthenaExportQueue.Count;
                AthenaExportQueue.Clear();
                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"Cleared the athena queue ({queued} cosmetics)", Constants.WHITE, true));
                return true;
            }
            case "Assets_Athena_Queue_Profile":
            {
                var queue = AthenaExportQueue.Snapshot();
                if (queue.Length == 0)
                {
                    FLogger.Append(ELog.Warning, () =>
                        FLogger.Text("The athena queue is empty, add cosmetics to it first", Constants.WHITE, true));
                    return true;
                }

                await _threadWorkerView.Begin(cancellationToken =>
                    AthenaProfileGenerator.Generate(queue, contextViewModel.CUE4Parse.Provider, cancellationToken,
                        contextViewModel.ReportAthenaProfileProgress));
                return true;
            }
            default:
                return false;
        }
    }

    private void LogExport(ApplicationViewModel contextViewModel, string directory, string path, string basePath, string fileType, int queuedBefore = 0)
    {
        var queuedDelta = ExportSessionViewModel.Instance.Session.TotalQueued - queuedBefore;
        if (contextViewModel.CUE4Parse.ExportedCount > 0)
        {
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text($"Successfully exported {contextViewModel.CUE4Parse.ExportedCount} {fileType} from ", Constants.WHITE);
                FLogger.Link(directory, Path.Exists(path) ? path : basePath, true);
            });
        }
        else if (queuedDelta > 0)
        {
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text($"Queued {queuedDelta} {fileType} for export from {directory}{(UserSettings.Default.ExportImmediately ? ", exporting automatically..." : "")}", Constants.WHITE, true);
            });
        }
        else if (contextViewModel.CUE4Parse.FailedExportCount == 0)
        {
            // Not an error because folder simply might not contain type of asset user is trying to save
            FLogger.Append(ELog.Warning, () =>
            {
                FLogger.Text($"Failed to find any {fileType} in {directory}", Constants.WHITE, true);
            });
        }

        if (contextViewModel.CUE4Parse.FailedExportCount > 0)
        {
            FLogger.Append(ELog.Error, () =>
            {
                FLogger.Text($"Failed to export {contextViewModel.CUE4Parse.FailedExportCount} {fileType} from {directory}", Constants.WHITE, true);
            });
        }

        Interlocked.Exchange(ref contextViewModel.CUE4Parse.ExportedCount, 0);
        Interlocked.Exchange(ref contextViewModel.CUE4Parse.FailedExportCount, 0);
    }
}
