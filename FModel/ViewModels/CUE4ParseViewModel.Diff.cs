using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Wwise;
using CUE4Parse_Conversion.Textures;
using FModel.Extensions;
using FModel.Framework;
using FModel.Settings;
using FModel.Views.Resources.Controls.Diff;
using Newtonsoft.Json;
using Serilog;
using SkiaSharp;
using Svg.Skia;

namespace FModel.ViewModels;

public partial class CUE4ParseViewModel
{
    /// <summary>
    /// Secondary provider the current game files are compared against. Null until a directory is picked.
    /// </summary>
    public AbstractVfsFileProvider DiffProvider { get; private set; }

    private string _diffDirectory;
    /// <summary>
    /// Directory <see cref="DiffProvider"/> was built from, shown in the diff header.
    /// </summary>
    public string DiffDirectory
    {
        get => _diffDirectory;
        private set => SetProperty(ref _diffDirectory, value);
    }

    public bool HasDiffProvider => DiffProvider is not null;

    /// <summary>
    /// Mounts <paramref name="gameDirectory"/> as the comparison side, reusing the versioning and the AES keys
    /// already accepted by the main provider.
    /// </summary>
    public void LoadDiffProvider(string gameDirectory)
    {
        UnloadDiffProvider();

        var currentDir = UserSettings.Default.CurrentDir;
        var versionContainer = new VersionContainer(
            game: currentDir.UeVersion, platform: currentDir.TexturePlatform,
            customVersions: new FCustomVersionContainer(currentDir.Versioning.CustomVersions),
            optionOverrides: currentDir.Versioning.Options,
            mapStructTypesOverrides: currentDir.Versioning.MapStructTypes);

        var provider = new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.OrdinalIgnoreCase)
        {
            ReadScriptData = Provider.ReadScriptData,
            ReadShaderMaps = Provider.ReadShaderMaps,
            ReadNaniteData = true
        };

        provider.Initialize();
        provider.SubmitKeys(Provider.Keys);
        provider.PostMount();

        DiffProvider = provider;
        DiffDirectory = gameDirectory;

        var archiveMax = provider.UnloadedVfs.Count + provider.MountedVfs.Count;
        Log.Information($"Diff provider: {gameDirectory} | Mounted: {provider.MountedVfs.Count}/{archiveMax} | AES: {provider.Keys.Count} | Files: x{provider.Files.Count}");

        RaisePropertyChanged(nameof(HasDiffProvider));
    }

    public void UnloadDiffProvider()
    {
        if (DiffProvider is null) return;

        DiffProvider.Dispose();
        DiffProvider = null;
        DiffDirectory = null;
        RaisePropertyChanged(nameof(HasDiffProvider));
    }

    public async Task ShowAssetDiff(string assetPath)
    {
        GameFile leftFile = TryGetFileByPathOrName(DiffProvider?.Files, assetPath);
        GameFile rightFile = TryGetFileByPathOrName(Provider?.Files, assetPath);

        var leftImage = LoadTabImageForDiff(DiffProvider, leftFile);
        var rightImage = LoadTabImageForDiff(Provider, rightFile);

        var titleExtra = Path.GetFileName(assetPath);
        string extension = Path.GetExtension(assetPath).TrimStart('.');

        await Application.Current.Dispatcher.Invoke(async () =>
        {
            var diffContent = await CreateDiffViewer(leftFile, rightFile, leftImage, rightImage, extension);
            SetDiffTabContent(titleExtra, diffContent);
        });
    }

    /// <summary>
    /// Pushes <paramref name="diffContent"/> into the single diff tab, creating it if it does not exist yet.
    /// Must be called on the UI thread.
    /// </summary>
    private void SetDiffTabContent(string titleExtra, object diffContent)
    {
        var existingTab = TabControl.TabsItems.FirstOrDefault(tab => tab.ParentExportType == "Diff");
        if (existingTab != null)
        {
            existingTab.TitleExtra = titleExtra;
            existingTab.DiffContent = diffContent;
            if (TabControl.SelectedTab != existingTab)
                TabControl.SelectedTab = existingTab;
        }
        else
        {
            var tab = new TabItem(new FakeGameFile("Diff Viewer"), "Diff")
            {
                TitleExtra = titleExtra,
                DiffContent = diffContent
            };
            TabControl.AddTab(tab);
            TabControl.SelectedTab = tab;
        }
    }

    private async Task<object> CreateDiffViewer(GameFile leftFile, GameFile rightFile, TabImage leftImage, TabImage rightImage, string extension)
    {
        if (leftImage != null || rightImage != null)
        {
            if (leftImage != null && leftImage.VisuallyEquals(rightImage))
            {
                return new SameDataMessage();
            }

            var viewer = new ImageDiffViewer();
            viewer.SetImages(leftImage, rightImage);
            return viewer;
        }

        var (l, r) = GetExtractedTextsForDiff(leftFile, rightFile);
        if (AreTextsEqual(l, r))
        {
            return new SameDataMessage();
        }

        var dataDiffViewer = new DataDiffViewer(l, r, extension);
        await dataDiffViewer.Initialize();

        return dataDiffViewer;
    }

    private static List<string> SplitIntoChunks(string text)
    {
        const int maxLinesPerChunk = 150_000;
        var lines = text.Split('\n');
        var chunks = new List<string>();

        for (int i = 0; i < lines.Length; i += maxLinesPerChunk)
        {
            var chunkLines = lines.Skip(i).Take(maxLinesPerChunk);
            chunks.Add(string.Join("\n", chunkLines));
        }
        return chunks;
    }

    private (List<string> left, List<string> right) GetExtractedTextsForDiff(GameFile leftFile, GameFile rightFile)
    {
        List<string> left = leftFile != null ? SplitIntoChunks(ExtractTextForDiff(DiffProvider, leftFile)) : [];
        List<string> right = rightFile != null ? SplitIntoChunks(ExtractTextForDiff(Provider, rightFile)) : [];

        return (left, right);
    }

    private static GameFile TryGetFileByPathOrName(FileProviderDictionary files, string assetPath)
    {
        if (files is null)
            return null;

        if (files.TryGetValue(assetPath, out var file))
            return file;

        var fileName = Path.GetFileName(assetPath);
        var matches = files.Where(kvp => Path.GetFileName(kvp.Key)!.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();

        switch (matches.Count)
        {
            case 1:
                return matches[0].Value;
            case > 1:
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var dialog = new DiffFileSelectionDialog(matches.Select(m => m.Value).ToList());
                    if (dialog.ShowDialog() == true)
                    {
                        file = dialog.SelectedFile;
                    }
                });
                return file;
            default:
                return null;
        }
    }

    private static string ExtractTextForDiff(AbstractVfsFileProvider provider, GameFile entry)
    {
        var ext = entry.Extension.ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case "uasset":
                case "umap":
                    {
                        var package = provider.GetLoadPackageResult(entry);
                        return JsonConvert.SerializeObject(package.GetDisplayData(), Formatting.Indented);
                    }
                // Common text-based formats
                case "json":
                case "manifest":
                case "uproject":
                case "uplugin":
                case "upluginmanifest":
                case "xml":
                case "ini":
                case "txt":
                case "log":
                case "bat":
                case "cfg":
                case "csv":
                case "pem":
                case "tps":
                case "tgc":
                case "lua":
                case "js":
                case "po":
                case "h":
                case "cpp":
                case "c":
                case "hpp":
                case "cs":
                case "vb":
                case "py":
                case "md":
                case "markdown":
                case "yml":
                case "yaml":
                case "sh":
                case "cmd":
                case "sql":
                case "css":
                case "scss":
                case "less":
                case "ts":
                case "tsx":
                case "jsx":
                case "html":
                case "htm":
                case "lsd":
                case "dat":
                case "ddr":
                case "ide":
                case "ipl":
                case "zon":
                case "verse":
                    {
                        var data = provider.SaveAsset(entry);
                        using var ms = new MemoryStream(data);
                        using var reader = new StreamReader(ms, true);
                        return reader.ReadToEnd();
                    }
                // Localization
                case "locmeta":
                    {
                        var archive = entry.CreateReader();
                        var metadata = new FTextLocalizationMetaDataResource(archive);
                        return JsonConvert.SerializeObject(metadata, Formatting.Indented);
                    }
                case "locres":
                    {
                        var archive = entry.CreateReader();
                        var locres = new FTextLocalizationResource(archive);
                        return JsonConvert.SerializeObject(locres, Formatting.Indented);
                    }
                // Asset registry
                case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
                    {
                        var archive = entry.CreateReader();
                        var registry = new FAssetRegistryState(archive);
                        return JsonConvert.SerializeObject(registry, Formatting.Indented);
                    }
                // Shader cache
                case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
                    {
                        var archive = entry.CreateReader();
                        var registry = new FGlobalShaderCache(archive);
                        return JsonConvert.SerializeObject(registry, Formatting.Indented);
                    }
                // Wwise
                case "bnk":
                case "pck":
                    {
                        var archive = entry.CreateReader();
                        var wwise = new WwiseReader(new FWwiseArchive(archive), new WwiseGameFileSource(entry));
                        return JsonConvert.SerializeObject(wwise, Formatting.Indented);
                    }
                // Oodle dictionary
                case "udic":
                    {
                        var archive = entry.CreateReader();
                        var header = new FOodleDictionaryArchive(archive).Header;
                        return JsonConvert.SerializeObject(header, Formatting.Indented);
                    }
                // Shader bytecode
                case "ushaderbytecode":
                case "ushadercode":
                    {
                        var archive = entry.CreateReader();
                        var ar = new FShaderCodeArchive(archive);
                        return JsonConvert.SerializeObject(ar, Formatting.Indented);
                    }
                // Pipeline cache
                case "upipelinecache":
                    {
                        var archive = entry.CreateReader();
                        var ar = new FPipelineCacheFile(archive);
                        return JsonConvert.SerializeObject(ar, Formatting.Indented);
                    }
                default:
                    {
                        // Fallback: if it's a small file, try to display as text
                        var data = provider.SaveAsset(entry);
                        if (data.Length < 1024 * 1024) // 1MB
                        {
                            try
                            {
                                using var ms = new MemoryStream(data);
                                using var reader = new StreamReader(ms, true);
                                return reader.ReadToEnd();
                            }
                            catch { /* Ignore binary files */ }
                        }
                        return $"[No diffable text for type: {ext}]";
                    }
            }
        }
        catch (Exception ex)
        {
            return $"[Failed to read this file for comparison: {ex.Message}]";
        }
    }

    private TabImage LoadTabImageForDiff(AbstractVfsFileProvider provider, GameFile entry)
    {
        if (entry == null)
            return null;

        var ext = entry.Extension.ToLowerInvariant();
        var name = entry.NameWithoutExtension;
        const bool rnn = false;

        switch (ext)
        {
            case "png":
            case "jpg":
            case "jpeg":
            case "bmp":
                {
                    var data = provider.SaveAsset(entry);
                    using var ms = new MemoryStream(data);
                    var bmp = SKBitmap.Decode(ms);
                    return bmp != null
                        ? new TabImage(name, rnn, bmp)
                        : null;
                }

            case "svg":
                {
                    var data = provider.SaveAsset(entry);
                    var bmp = RenderSvgToBitmap(data);
                    return bmp != null
                        ? new TabImage(name, rnn, bmp)
                        : null;
                }
            case "uasset":
                {
                    try
                    {
                        var pkg = provider.LoadPackage(entry);

                        var pointer = new FPackageIndex(pkg, 1).ResolvedObject;

                        if (pointer?.Object?.Value is not UTexture texture)
                            return null;

                        CTexture[] textures;
                        if (texture is UTexture2DArray arr)
                            textures = arr.DecodeTextureArray(UserSettings.Default.CurrentDir.TexturePlatform);
                        else
                        {
                            var single = texture.Decode(UserSettings.Default.CurrentDir.TexturePlatform);
                            if (texture is UTextureCube)
                            {
                                single = single?.ToPanorama();
                            }

                            textures = [single];
                        }

                        if (textures != null)
                        {
                            var ct = textures.FirstOrDefault();
                            return ct != null
                                ? new TabImage(name, texture.RenderNearestNeighbor, ct)
                                : null;
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Warning("Failed to decode UTexture for diff: {EntryPath} – {Message}", entry.Path, e.Message);
                        return null;
                    }

                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// Rasterizes SVG bytes the same way the regular asset viewer does, so both sides of a diff match.
    /// </summary>
    private static SKBitmap RenderSvgToBitmap(byte[] data, int size = 512)
    {
        using var stream = new MemoryStream(data) { Position = 0 };
        var svg = new SKSvg();
        svg.Load(stream);

        if (svg.Picture == null) return null;

        var bitmap = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var bounds = svg.Picture.CullRect;
        var scale = Math.Min(size / bounds.Width, size / bounds.Height);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(svg.Picture);

        return bitmap;
    }

    private static bool AreTextsEqual(List<string> leftChunks, List<string> rightChunks)
    {
        if ((leftChunks == null || leftChunks.Count == 0) && (rightChunks == null || rightChunks.Count == 0))
            return true;

        if (leftChunks == null || rightChunks == null)
            return false;

        if (leftChunks.Count != rightChunks.Count)
            return false;

        // comparing the chunks directly bails out on the first difference instead of hashing megabytes twice
        for (int i = 0; i < leftChunks.Count; i++)
        {
            if (!string.Equals(leftChunks[i], rightChunks[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}