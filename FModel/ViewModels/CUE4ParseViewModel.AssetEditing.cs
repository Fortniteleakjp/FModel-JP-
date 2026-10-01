using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse.FileProvider.Objects;
using FModel.Framework;
using FModel.Services.AssetEditing;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Serilog;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.ViewModels;

public partial class CUE4ParseViewModel
{
    /// <summary>
    /// where packages written back from edited json go, mirroring the game's directory structure so they can be repacked as is
    /// </summary>
    public static string EditedAssetsDirectory => Path.Combine(UserSettings.Default.OutputDirectory, "Edited");

    public static void OpenEditedAssetsDirectory(GameFile entry)
    {
        var directory = EditedAssetsDirectory;
        if (entry != null)
        {
            var assetDirectory = Path.Combine(directory, entry.Directory.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(assetDirectory)) directory = assetDirectory;
        }

        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception e)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"フォルダーを開けませんでした: {e.Message}", Constants.WHITE, true));
        }
    }

    /// <summary>
    /// where the OBJ to reshape a mesh is written, outside <see cref="EditedAssetsDirectory"/> so it never ends up in a pak
    /// </summary>
    public static string MeshEditPath(GameFile entry) =>
        Path.Combine(UserSettings.Default.OutputDirectory, "MeshEdit", entry.PathWithoutExtension.Replace('/', Path.DirectorySeparatorChar) + ".obj");

    public void ExportMeshForEditing(GameFile entry)
    {
        try
        {
            var mesh = MeshPatcher.FindMesh(Provider.LoadPackage(entry), entry.NameWithoutExtension, out var lods, out var error);
            if (mesh == null)
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"編集用 OBJ を書き出せません: {error}", Constants.WHITE, true));
                return;
            }

            var path = MeshEditPath(entry);
            MeshPatcher.ExportObj(mesh, lods, path);
            Log.Information("Exported {Mesh} for editing to {Path}", mesh.Name, path);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("編集用 OBJ を書き出しました（頂点の数と順序を変えずに変形し、「OBJ でメッシュを差し替え」で読み込んでください）: ", Constants.WHITE);
                FLogger.Link(Path.GetFileName(path), path, true);
            });
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not export {Path} for mesh editing", entry.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"編集用 OBJ を書き出せませんでした: {e.Message}", Constants.WHITE, true));
        }
    }

    /// <summary>
    /// writes the package shown in <paramref name="tab"/> back to .uasset with the edits made to its json
    /// </summary>
    public void SaveEditedJson(TabItem tab)
    {
        string edited = null, original = null, image = null, mesh = null;
        GameFile entry = null;
        var exportStart = 0;
        Application.Current.Dispatcher.Invoke(() =>
        {
            edited = tab.Document?.Text;
            original = tab.EditBaseJson;
            entry = tab.Entry;
            exportStart = tab.ExportPageStart;
            image = tab.ReplacementImagePath;
            mesh = tab.ReplacementMeshPath;
            tab.EditStatus = "書き出し中...";
        });
        if (original == null || edited == null || entry == null) return;

        AssetEditReport report;
        try
        {
            report = EditedAssetWriter.Write(new EditedAssetWriter.Request
            {
                Provider = Provider,
                Entry = entry,
                OriginalJson = original,
                EditedJson = edited,
                ExportStart = exportStart,
                ReplacementImagePath = image,
                ReplacementMeshPath = mesh,
                OutputDirectory = EditedAssetsDirectory
            });
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not write edited {Path}", entry.Path);
            report = new AssetEditReport();
            report.Error(null, e.Message);
        }

        Report(tab, entry, report);
    }

    private static void Report(TabItem tab, GameFile entry, AssetEditReport report)
    {
        if (report.HasErrors)
        {
            Log.Warning("Could not write edited {Path}: {Errors}", entry.Path, string.Join(" | ", report.Errors));
            FLogger.Append(ELog.Error, () => FLogger.Text($"'{entry.Name}' を .uasset に書き戻せませんでした（{report.Errors.Count} 件のエラー）", Constants.WHITE, true));
            Application.Current.Dispatcher.Invoke(() =>
            {
                tab.EditStatus = $"書き出せませんでした: {report.Errors[0]}";
                MessageBox.Show(Lines("書き戻せませんでした。", report.Errors, report.Warnings), "JSON → uasset", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
            return;
        }

        Log.Information("Wrote edited {Path} ({Count} changes, verified {Verified})", entry.Path, report.Changes.Count, report.Verified);
        foreach (var sideEffect in report.SideEffects)
            Log.Information("Untouched value read back differently: {SideEffect}", sideEffect);
        var main = report.OutputFiles.FirstOrDefault() ?? EditedAssetsDirectory;
        FLogger.Append(ELog.Information, () =>
        {
            FLogger.Text($"編集した JSON を .uasset に書き出しました（変更 {report.Changes.Count} 件）: ", Constants.WHITE);
            FLogger.Link(Path.GetFileName(main), main, true);
        });

        foreach (var warning in report.Warnings.Take(5))
            FLogger.Append(ELog.Warning, () => FLogger.Text(warning, Constants.WHITE, true));

        string status;
        if (report.Verified)
        {
            status = $"{DateTime.Now:HH:mm:ss} 書き出し完了・検証OK（変更 {report.Changes.Count} 件、読み直した値が編集内容と一致）";
        }
        else if (report.VerificationMismatches.Count > 0)
        {
            status = $"{DateTime.Now:HH:mm:ss} 書き出し完了・検証で {report.VerificationMismatches.Count} 件の不一致: {report.VerificationMismatches[0]}";
            foreach (var mismatch in report.VerificationMismatches.Take(5))
                FLogger.Append(ELog.Warning, () => FLogger.Text($"検証の不一致 {mismatch}", Constants.WHITE, true));
        }
        else
        {
            status = $"{DateTime.Now:HH:mm:ss} 書き出し完了（変更 {report.Changes.Count} 件、検証は実行できませんでした）";
        }

        Application.Current.Dispatcher.Invoke(() => tab.EditStatus = status);
        if (report.ReloadedTexture != null) tab.AddReplacedTexture(report.ReloadedTexture);
    }

    private static string Lines(string title, System.Collections.Generic.IReadOnlyList<string> errors, System.Collections.Generic.IReadOnlyList<string> warnings)
    {
        var text = title + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors.Take(15).Select(e => "・" + e));
        if (errors.Count > 15) text += $"{Environment.NewLine}…ほか {errors.Count - 15} 件";
        if (warnings.Count > 0) text += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, warnings.Take(5).Select(w => "※" + w));
        return text;
    }
}
