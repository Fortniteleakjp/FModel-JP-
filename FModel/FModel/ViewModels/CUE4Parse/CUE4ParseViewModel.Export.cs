using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AdonisUI.Controls;
using Microsoft.Win32;
using CUE4Parse;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Usmap; // 荳頑ｵ∝酔譛・ FileUsmapTypeMappingsProvider 縺・Usmap/ 繧ｵ繝門錐蜑咲ｩｺ髢薙∈遘ｻ蜍・
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.CriWare;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.CriWare;
using CUE4Parse.UE4.CriWare.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.Engine.Animation;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using EpicManifestParser;
using EpicManifestParser.UE;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using K4os.Compression.LZ4.Streams;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using Svg.Skia;
using UE4Config.Parsing;
using Application = System.Windows.Application;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using K4os.Compression.LZ4;

namespace FModel.ViewModels.CUE4Parse;

public partial class CUE4ParseViewModel
{
    public string Decompile(GameFile entry, bool addTab = true)
    {
        if (addTab) {
            TabControl.AddTab(entry, "Decompiled");
        }

        UClassCookedMetaData cookedMetaData = null;
        try
        {
            var editorPkg = Provider.LoadPackage(entry.Path.Replace(".uasset", ".o.uasset"));
            cookedMetaData = editorPkg.GetExport<UClassCookedMetaData>("CookedClassMetaData");
        }
        catch
        {
            // ignored
        }

        var cppList = new List<string>();
        var pkg = Provider.LoadPackage(entry);
        for (var i = 0; i < pkg.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(pkg, i + 1).ResolvedObject;
            if (pointer?.Object is null && pointer.Class?.Object?.Value is null)
                continue;

            var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class, pkg);
            if (dummy is not UClass || pointer.Object.Value is not UClass blueprint)
                continue;

            cppList.Add(blueprint.DecompileBlueprintToPseudo(cookedMetaData));
        }

        var cpp = cppList.Count > 1 ? string.Join("\n\n", cppList) : cppList.FirstOrDefault() ?? string.Empty;
        if (entry.Path.Contains("_Verse.uasset"))
        {
            cpp = Regex.Replace(cpp, "__verse_0x[a-fA-F0-9]{8}_", ""); // UnmangleCasedName
        }
        cpp = Regex.Replace(cpp, @"CallFunc_([A-Za-z0-9_]+)_ReturnValue", "$1");

        return cpp;
    }

    private void SaveAndPlaySound(string fullPath, string ext, byte[] data, bool isBulk)
    {
        if (fullPath.StartsWith('/')) fullPath = fullPath[1..];
        var savedAudioPath = Path.Combine(UserSettings.Default.AudioDirectory,
            UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/')).Replace('\\', '/') + $".{ext.ToLowerInvariant()}";

        if (isBulk)
        {
            Directory.CreateDirectory(savedAudioPath.SubstringBeforeLast('/'));
            if (ext.Equals("rada", StringComparison.OrdinalIgnoreCase) || ext.Equals("binka", StringComparison.OrdinalIgnoreCase))
            {
                // Convert to WAV
                var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                if (AudioPlayerViewModel.TryDecode(ext, tempPath, data, out var wavPath))
                {
                    File.Move(wavPath, savedAudioPath.Replace($".{ext.ToLowerInvariant()}", ".wav"));
                    Log.Information("Successfully saved {FilePath}", savedAudioPath.Replace($".{ext.ToLowerInvariant()}", ".wav"));
                }
                else
                {
                    // Fallback to original
                    using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
                    using var writer = new BinaryWriter(stream);
                    writer.Write(data);
                    writer.Flush();
                    Log.Error("Failed to convert {FilePath}", savedAudioPath);
                }
            }
            else
            {
                using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
                using var writer = new BinaryWriter(stream);
                writer.Write(data);
                writer.Flush();
            }
            return;
        }

        // TODO
        // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
        // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
        // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
        Application.Current.Dispatcher.Invoke(delegate
        {
            var audioPlayer = (AudioPlayer) Helper.GetWindow<AdonisUI.Controls.AdonisWindow>("Audio Player", () => (AdonisUI.Controls.AdonisWindow)new AudioPlayer());
            audioPlayer.Load(data, savedAudioPath);
        });
    }

    private void SaveExport(UObject export, bool updateUi = true)
    {
        // 旧Exporter APIは廃止し、すべてExportSessionで処理する。
        TrySaveExportNewPipeline(export, updateUi);
    }

    /// <summary>
    /// 譁ｰ繧ｨ繧ｯ繧ｹ繝昴・繝医ヱ繧､繝励Λ繧､繝ｳ・・UE4Parse PR #358 縺ｮ ExportSession 繝吶・繧ｹ・峨〒縺ｮ譖ｸ縺榊・縺励・
    /// 谿ｵ髫主ｰ主・荳ｭ: 譛ｪ蟇ｾ蠢懊・蝙具ｼ・dd 縺・NotSupported・峨ｄ螳溯｡梧凾縺ｮ莠域悄縺帙〓螟ｱ謨玲凾縺ｯ false 繧定ｿ斐＠縲・
    /// 蜻ｼ縺ｳ蜃ｺ縺怜・縺梧立繝代う繝励Λ繧､繝ｳ縺ｸ繝輔か繝ｼ繝ｫ繝舌ャ繧ｯ縺吶ｋ縲ら樟迥ｶ繝｡繝・す繝･縺ｯ USD 蠖｢蠑上〒譖ｸ縺榊・縺吶・
    /// </summary>
    private bool TrySaveExportNewPipeline(UObject export, bool updateUi)
    {
        // 蜃ｺ蜉帙Γ繝・す繝･蠖｢蠑上ｒ譌ｧ險ｭ螳・Meshes.EMeshFormat)縺九ｉ譁ｰ(Options.EMeshFormat)縺ｸ繝槭ャ繝・蠎乗焚荳閾ｴ)縲・
        // 蜈ｨ繝｡繝・す繝･蠖｢蠑・ActorX/glTF/OBJ/UEFormat/USD)縺梧眠繝代う繝励Λ繧､繝ｳ縺ｫ遘ｻ讀肴ｸ医∩縺ｮ縺溘ａ蠖｢蠑上ご繝ｼ繝医・荳崎ｦ√・
        var meshFormat = (CUE4Parse_Conversion.Options.EMeshFormat)(int)UserSettings.Default.MeshExportFormat;
        // World(UWorld) 縺ｯ WorldExporter 縺・USD 蠖｢蠑上・縺ｿ蟇ｾ蠢懊・縺溘ａ縲∬ｨｭ螳壹↓髢｢繧上ｉ縺・USD 繧貞ｼｷ蛻ｶ縺吶ｋ縲・
        if (export is UWorld)
            meshFormat = CUE4Parse_Conversion.Options.EMeshFormat.USD;

        CUE4Parse_Conversion.ExportSession session;
        try
        {
            session = new CUE4Parse_Conversion.ExportSession();
            session.Add(export); // 譛ｪ蟇ｾ蠢懊・蝙九・縺薙％縺ｧ NotSupportedException
        }
        catch (NotSupportedException)
        {
            return false; // 譛ｪ蟇ｾ蠢懊・蝙・竊・譌ｧ繝代う繝励Λ繧､繝ｳ縺ｸ繝輔か繝ｼ繝ｫ繝舌ャ繧ｯ
        }

        ExportSessionViewModel.Instance.BeginExport(export.Name);
        try
        {
            var legacy = UserSettings.Default.ExportOptions;
            var options = new CUE4Parse_Conversion.Options.ExportOptions(
                meshFormat: meshFormat,
                naniteMeshFormat: legacy.NaniteMeshFormat,
                texturePlatform: legacy.TexturePlatform,
                exportHdrTexturesAsHdr: legacy.ExportHdrTexturesAsHdr,
                exportMaterials: legacy.ExportMaterials,
                exportMorphTargets: legacy.ExportMorphTargets);

            // Export Session 繧ｦ繧､繝ｳ繝峨え縺ｸ騾ｲ謐励ｒ蝣ｱ蜻奇ｼ亥・蜉帛・縺ｯ繧ｻ繝・す繝ｧ繝ｳ荳頑嶌縺崎ｨｭ螳壹′縺ゅｌ縺ｰ縺昴■繧峨ｒ菴ｿ逕ｨ・・
            var results = session.RunAsync(ExportSessionViewModel.Instance.EffectiveModelDirectory, options, ExportSessionViewModel.Instance).GetAwaiter().GetResult();

            string savedPath = null;
            Exception error = null;
            foreach (var r in results)
            {
                if (r.Success) savedPath ??= r.DiskFilePath;
                else error ??= r.Error;
            }

            if (savedPath != null)
            {
                Log.Information("Successfully saved {FilePath} (new pipeline)", savedPath);
                if (updateUi)
                {
                    var link = savedPath;
                    FLogger.Append(ELog.Information, () =>
                    {
                        FLogger.Text("Successfully saved (new pipeline) ", Constants.WHITE);
                        FLogger.Link(export.Name, link, true);
                    });
                }
            }
            else
            {
                Log.Error(error, "{FileName} could not be saved (new pipeline)", export.Name);
                if (updateUi)
                    FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{export.Name}' (new pipeline)", Constants.WHITE, true));
            }

            return true; // 蝙九・蟇ｾ蠢懈ｸ医∩ 竊・譁ｰ繝代う繝励Λ繧､繝ｳ縺ｧ蜃ｦ逅・ｸ医∩・域立縺ｸ縺ｯ蝗槭＆縺ｪ縺・ｼ・
        }
        catch (Exception ex)
        {
            Log.Error(ex, "New export pipeline threw for {Name}", export.Name);
            return false; // 螳溯｡梧凾縺ｮ莠域悄縺帙〓螟ｱ謨励・縺ｿ譌ｧ繝代う繝励Λ繧､繝ｳ縺ｸ繝輔か繝ｼ繝ｫ繝舌ャ繧ｯ
        }
        finally
        {
            ExportSessionViewModel.Instance.EndExport(export.Name);
        }
    }

    private readonly object _rawData = new ();
    public void ExportData(GameFile entry, bool updateUi = true)
    {
        if (Provider.TrySavePackage(entry, out var assets))
        {
            string path = UserSettings.Default.RawDataDirectory;
            Parallel.ForEach(assets, kvp =>
            {
                lock (_rawData)
                {
                    path = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/')).Replace('\\', '/');
                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                    File.WriteAllBytes(path, kvp.Value);
                }
            });

            Log.Information("{FileName} successfully exported", entry.Name);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully exported ", Constants.WHITE);
                    FLogger.Link(entry.Name, path, true);
                });
            }
        }
        else
        {
            Log.Error("{FileName} could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not export '{entry.Name}'", Constants.WHITE, true));
        }
    }
}
