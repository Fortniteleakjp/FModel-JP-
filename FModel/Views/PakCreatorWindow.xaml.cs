using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FModel.Framework;
using FModel.Services;
using FModel.Services.AssetEditing;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views.Resources.Controls;
using Ookii.Dialogs.Wpf;
using Serilog;

namespace FModel.Views;

/// <summary>
/// packs the packages written by the json/texture/mesh edits (Output/Edited) into a .pak,
/// with the merged AssetRegistry and the .sig the game expects next to it
/// </summary>
public partial class PakCreatorWindow
{
    private List<(string fullPath, string pakPath)> _files = [];
    private string _autoRegistryName;

    public PakCreatorWindow()
    {
        InitializeComponent();

        SourceBox.Text = CUE4ParseViewModel.EditedAssetsDirectory;
        OutputBox.Text = !string.IsNullOrEmpty(UserSettings.Default.PakOutputPath)
            ? UserSettings.Default.PakOutputPath
            : Path.Combine(UserSettings.Default.OutputDirectory, "Paks", "FModelEdited_P.pak");
        MountBox.Text = string.IsNullOrEmpty(UserSettings.Default.PakMountPoint) ? "../../../" : UserSettings.Default.PakMountPoint;
        VersionBox.SelectedItem = VersionBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string) i.Tag == UserSettings.Default.PakVersion.ToString()) ?? VersionBox.Items[0];

        // the registries of the loaded game, the merged one replaces the game's file at the same path
        var registries = GameRegistries();
        RegistryBaseBox.ItemsSource = registries;
        RegistryBaseBox.Text = !string.IsNullOrEmpty(UserSettings.Default.PakRegistryBase) ? UserSettings.Default.PakRegistryBase : registries.FirstOrDefault() ?? "";
        RegistrySourceBox.Text = UserSettings.Default.PakRegistrySource ?? "";
        RegistryFilterBox.Text = UserSettings.Default.PakRegistryFilter ?? "";
        _autoRegistryName = DefaultRegistryName(RegistryBaseBox.Text);
        RegistryNameBox.Text = !string.IsNullOrEmpty(UserSettings.Default.PakRegistryName) ? UserSettings.Default.PakRegistryName : _autoRegistryName;
        RegistryCheck.IsChecked = UserSettings.Default.PakMergeRegistry;

        SignatureTemplateBox.Text = !string.IsNullOrEmpty(UserSettings.Default.PakSignatureTemplate) && File.Exists(UserSettings.Default.PakSignatureTemplate)
            ? UserSettings.Default.PakSignatureTemplate
            : PakWriter.FindSignatureTemplate(UserSettings.Default.GameDirectory, OutputBox.Text) ?? "";
        SignatureCheck.IsChecked = UserSettings.Default.PakWriteSignature;

        UpdateOptions();
        Refresh();
    }

    private void Refresh()
    {
        _files = PakWriter.Collect(SourceBox.Text);
        FileList.ItemsSource = _files.Select(f => f.pakPath).ToList();
        SummaryText.Text = _files.Count == 0
            ? "pak に入れるファイルがありません（JSON 編集・画像/メッシュ差し替えで書き出したものがここに入ります）"
            : $"{_files.Count} ファイル（{_files.Sum(f => new FileInfo(f.fullPath).Length) / 1024.0 / 1024.0:0.0} MB）。pak 内のパス = マウントポイント + 以下のパス";
        CreateButton.IsEnabled = _files.Count > 0;
    }

    private static List<string> GameRegistries()
    {
        try
        {
            return ApplicationService.ApplicationView.CUE4Parse.Provider.Files.Values
                .Where(f => f.Name.StartsWith("AssetRegistry", StringComparison.OrdinalIgnoreCase) && f.Extension.Equals("bin", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Path)
                .OrderBy(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string DefaultRegistryName(string basePath) =>
        string.IsNullOrWhiteSpace(basePath) ? "AssetRegistry.bin" : Path.GetFileName(basePath.Trim().Trim('"').Replace('\\', '/'));

    private void UpdateOptions()
    {
        var registry = RegistryCheck.IsChecked == true;
        RegistryBaseBox.IsEnabled = RegistryBaseBrowse.IsEnabled = RegistrySourceBox.IsEnabled = RegistrySourceBrowse.IsEnabled =
            RegistryFilterBox.IsEnabled = RegistryNameBox.IsEnabled = registry;
        SignatureTemplateBox.IsEnabled = SignatureTemplateBrowse.IsEnabled = SignatureCheck.IsChecked == true;
    }

    private void OnRegistryToggled(object sender, RoutedEventArgs e) => UpdateOptions();
    private void OnSignatureToggled(object sender, RoutedEventArgs e) => UpdateOptions();

    private void OnRegistryBaseChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var name = DefaultRegistryName(RegistryBaseBox.SelectedItem as string ?? RegistryBaseBox.Text);
        // follow the base file name unless another name was typed
        if (string.IsNullOrWhiteSpace(RegistryNameBox.Text) || RegistryNameBox.Text == _autoRegistryName) RegistryNameBox.Text = name;
        _autoRegistryName = name;
    }

    private void OnSourceChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) Refresh();
    }

    private void OnBrowseSource(object sender, RoutedEventArgs e)
    {
        var dialog = new VistaFolderBrowserDialog { SelectedPath = SourceBox.Text, UseDescriptionForTitle = true, Description = "pak に入れるフォルダー（ゲーム内のパス構成のもの）" };
        if (dialog.ShowDialog(this) == true) SourceBox.Text = dialog.SelectedPath;
    }

    private void OnBrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "作成する pak",
            Filter = "pak (*.pak)|*.pak",
            FileName = Path.GetFileName(OutputBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(OutputBox.Text)) ? Path.GetDirectoryName(OutputBox.Text) : null
        };
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FileName;
    }

    private void OnBrowseRegistryBase(object sender, RoutedEventArgs e)
    {
        var path = BrowseFile("元にする AssetRegistry（ゲームのもの）", "AssetRegistry (*.bin)|*.bin|すべてのファイル (*.*)|*.*", RegistryBaseBox.Text);
        if (path == null) return;
        RegistryBaseBox.Text = path;
        OnRegistryBaseChanged(sender, e);
    }

    private void OnBrowseRegistrySource(object sender, RoutedEventArgs e)
    {
        var path = BrowseFile("マージする AssetRegistry（クックした AssetRegistry.bin）", "AssetRegistry (*.bin)|*.bin|すべてのファイル (*.*)|*.*", RegistrySourceBox.Text);
        if (path != null) RegistrySourceBox.Text = path;
    }

    private void OnBrowseSignatureTemplate(object sender, RoutedEventArgs e)
    {
        var path = BrowseFile("署名部分をコピーする .sig（ゲームのもの）", "pak の署名 (*.sig)|*.sig", SignatureTemplateBox.Text);
        if (path != null) SignatureTemplateBox.Text = path;
    }

    private string BrowseFile(string title, string filter, string current)
    {
        var directory = File.Exists(current) ? Path.GetDirectoryName(current)
            : Directory.Exists(UserSettings.Default.GameDirectory) ? UserSettings.Default.GameDirectory : null;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, InitialDirectory = directory };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    /// <summary>
    /// the merged registry goes where the game has the base one, or at the project root ("FortniteGame/AssetRegistry.bin") when it comes from a file
    /// </summary>
    private string RegistryPakPath(string basePath, string name, bool fromGame)
    {
        if (fromGame)
        {
            var directory = Path.GetDirectoryName(basePath.Replace('\\', '/'))?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory)) return $"{directory}/{name}";
        }

        var project = _files.Select(f => f.pakPath.Split('/')[0])
            .Where(p => !p.Equals("Engine", StringComparison.OrdinalIgnoreCase) && !p.Contains('.'))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        project ??= ApplicationService.ApplicationView.CUE4Parse.Provider.ProjectName;
        return string.IsNullOrEmpty(project) ? name : $"{project}/{name}";
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var output = OutputBox.Text.Trim();
        var mountPoint = MountBox.Text.Trim();
        var version = int.Parse((string) ((ComboBoxItem) VersionBox.SelectedItem).Tag);
        if (string.IsNullOrEmpty(output) || !output.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "出力先は .pak ファイルを指定してください";
            return;
        }

        var mergeRegistry = RegistryCheck.IsChecked == true;
        var registryBase = (RegistryBaseBox.Text ?? "").Trim().Trim('"');
        var registrySource = RegistrySourceBox.Text.Trim().Trim('"');
        var registryFilter = RegistryFilterBox.Text.Trim();
        var registryName = RegistryNameBox.Text.Trim();
        var writeSignature = SignatureCheck.IsChecked == true;
        var signatureTemplate = SignatureTemplateBox.Text.Trim().Trim('"');
        if (mergeRegistry)
        {
            if (string.IsNullOrEmpty(registryBase) || string.IsNullOrEmpty(registrySource) || string.IsNullOrEmpty(registryName))
            {
                StatusText.Text = "AssetRegistry のマージには、元にするもの・マージするもの・pak 内の名前を指定してください";
                return;
            }
            if (!File.Exists(registrySource))
            {
                StatusText.Text = $"マージする AssetRegistry が見つかりません: {registrySource}";
                return;
            }
        }
        if (writeSignature && !string.IsNullOrEmpty(signatureTemplate) && !File.Exists(signatureTemplate))
        {
            StatusText.Text = $"署名をコピーする .sig が見つかりません: {signatureTemplate}";
            return;
        }

        UserSettings.Default.PakOutputPath = output;
        UserSettings.Default.PakVersion = version;
        UserSettings.Default.PakMountPoint = mountPoint;
        UserSettings.Default.PakMergeRegistry = mergeRegistry;
        UserSettings.Default.PakRegistryBase = registryBase;
        UserSettings.Default.PakRegistrySource = registrySource;
        UserSettings.Default.PakRegistryFilter = registryFilter;
        UserSettings.Default.PakRegistryName = registryName == _autoRegistryName ? null : registryName;
        UserSettings.Default.PakWriteSignature = writeSignature;
        UserSettings.Default.PakSignatureTemplate = signatureTemplate;

        Refresh();
        var files = _files.ToList();
        CreateButton.IsEnabled = false;
        StatusText.Text = "作成中...";
        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;
        var versions = provider.Versions;
        var registryFromGame = mergeRegistry && !File.Exists(registryBase);
        var registryPakPath = mergeRegistry ? RegistryPakPath(registryBase, registryName, registryFromGame) : null;

        PakWriter.Result result = null;
        AssetRegistryMerger.Result registry = null;
        string sigPath = null;
        string error = null;
        await Task.Run(() =>
        {
            try
            {
                if (mergeRegistry)
                {
                    byte[] baseData;
                    if (!registryFromGame) baseData = File.ReadAllBytes(registryBase);
                    else if (!provider.TrySaveAsset(registryBase, out baseData))
                        throw new FileNotFoundException($"元にする AssetRegistry がファイルにもゲーム内にも見つかりません: {registryBase}");

                    registry = AssetRegistryMerger.Merge(baseData, File.ReadAllBytes(registrySource), AssetRegistryMerger.ParseFilters(registryFilter));
                    if (registry.VerificationErrors.Count > 0)
                        throw new InvalidDataException($"マージした AssetRegistry の検証に失敗しました: {registry.VerificationErrors[0]}");

                    var registryFile = Path.Combine(Path.GetTempPath(), "FModel", "PakRegistry", registryName);
                    Directory.CreateDirectory(Path.GetDirectoryName(registryFile)!);
                    File.WriteAllBytes(registryFile, registry.Data);
                    files.RemoveAll(f => f.pakPath.Equals(registryPakPath, StringComparison.OrdinalIgnoreCase));
                    files.Add((registryFile, registryPakPath));
                }

                result = PakWriter.Write(files, output, version, mountPoint);
                PakWriter.Verify(result, files, versions);

                if (writeSignature)
                {
                    sigPath = PakWriter.WriteSignature(output, string.IsNullOrEmpty(signatureTemplate) ? null : signatureTemplate);
                    PakWriter.VerifySignature(result, sigPath);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not create {Pak}", output);
                error = ex.Message;
            }
        });

        CreateButton.IsEnabled = true;
        if (error != null)
        {
            StatusText.Text = $"作成できませんでした: {error}";
            return;
        }

        var verified = result.VerificationErrors.Count == 0;
        var lines = new List<string>
        {
            verified
                ? $"{Path.GetFileName(output)} を作成しました（{result.Files.Count} ファイル、{result.Size / 1024.0 / 1024.0:0.0} MB、読み直して全ファイル一致）"
                : $"作成しましたが検証で {result.VerificationErrors.Count} 件の不一致: {result.VerificationErrors[0]}"
        };
        if (registry != null)
        {
            lines.Add(registry.Added.Count > 0
                ? $"AssetRegistry に {registry.Added.Count} 件追加{(registry.AlreadyInBase.Count > 0 ? $"（{registry.AlreadyInBase.Count} 件は登録済みのため元のまま）" : "")} → {registryPakPath}"
                : $"⚠ フィルターに合う新しいアセットがなく、AssetRegistry は元のままです → {registryPakPath}");
        }
        if (sigPath != null)
            lines.Add(string.IsNullOrEmpty(signatureTemplate) ? $"{Path.GetFileName(sigPath)}（署名部分は空）" : $"{Path.GetFileName(sigPath)}（署名部分は {Path.GetFileName(signatureTemplate)} から）");
        StatusText.Text = string.Join("\n", lines);

        FLogger.Append(verified ? ELog.Information : ELog.Warning, () =>
        {
            FLogger.Text($"pak を作成しました（v{version}, {result.Files.Count} ファイル" +
                         $"{(verified ? "、検証OK" : $"、検証で {result.VerificationErrors.Count} 件の不一致")}" +
                         $"{(registry != null ? $"、AssetRegistry +{registry.Added.Count}" : "")}{(sigPath != null ? "、.sig 付き" : "")}）: ", Constants.WHITE);
            FLogger.Link(Path.GetFileName(output), output, true);
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
