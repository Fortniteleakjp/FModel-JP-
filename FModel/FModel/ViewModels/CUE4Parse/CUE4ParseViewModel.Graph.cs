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
using CUE4Parse.MappingsProvider.Usmap; // 上流同期: FileUsmapTypeMappingsProvider が Usmap/ サブ名前空間へ移動
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
using EpicManifestParser.ZlibngDotNetDecompressor;
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
    public void ShowMetadata(GameFile entry)
    {
        if (entry is null)
        {
            Log.Warning("ShowMetadata called with null entry");
            return;
        }

        if (!entry.IsUePackage)
        {
            FLogger.Append(ELog.Warning, () =>
            {
                FLogger.Text("Cannot show metadata for non-UE package: ", Constants.WHITE);
                FLogger.Text(entry.Path, Constants.GRAY, true);
            });
            return;
        }

        if (!Provider.TryLoadPackage(entry, out var package) || package is null)
        {
            FLogger.Append(ELog.Error, () =>
            {
                FLogger.Text("Failed to load package metadata: ", Constants.WHITE);
                FLogger.Text(entry.Path, Constants.GRAY, true);
            });
            return;
        }

        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Metadata";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("");

        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(package, Formatting.Indented), false, false);
    }

    public void ShowAnimGraph(GameFile entry)
    {
        if (!UserSettings.Default.EnableAnimGraphViewer)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Asset Graph Viewer is disabled in settings", Constants.WHITE, true));
            return;
        }

        if (entry is null)
        {
            Log.Warning("ShowAnimGraph called with null entry");
            return;
        }

        if (!entry.IsUePackage)
        {
            FLogger.Append(ELog.Warning, () =>
            {
                FLogger.Text("Cannot open graph viewer for non-UE package: ", Constants.WHITE);
                FLogger.Text(entry.Path, Constants.GRAY, true);
            });
            return;
        }

        if (!Provider.TryLoadPackage(entry, out var package) || package is null)
        {
            FLogger.Append(ELog.Error, () =>
            {
                FLogger.Text("Failed to load package for graph viewer: ", Constants.WHITE);
                FLogger.Text(entry.Path, Constants.GRAY, true);
            });
            return;
        }

        var export = package.GetExports().FirstOrDefault();
        if (export is null)
        {
            FLogger.Append(ELog.Warning, () =>
            {
                FLogger.Text("No exports found for graph viewer: ", Constants.WHITE);
                FLogger.Text(entry.Path, Constants.GRAY, true);
            });
            return;
        }

        var graphVm = AnimGraphViewModel.ExtractFromObject(export);
        _ = TryOpenGraphViewer(export, graphVm);
    }

    private static bool TryOpenGraphViewer(UObject asset, AnimGraphViewModel? existingViewModel = null)
    {
        if (!UserSettings.Default.EnableAnimGraphViewer)
            return false;

        var graphVm = existingViewModel ?? AnimGraphViewModel.ExtractFromObject(asset);
        if (graphVm.Nodes.Count == 0)
            graphVm = CreateFallbackAssetGraphViewModel(asset);

        if (graphVm.Nodes.Count == 0)
            return false;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var windowTitle = Application.Current.TryFindResource("AnimGraph_Title") as string ?? "Asset Graph Viewer";
            Helper.OpenWindow<AnimGraphViewer>(windowTitle, () =>
            {
                new AnimGraphViewer(graphVm).Show();
            });
        });

        return true;
    }

    private static AnimGraphViewModel CreateFallbackAssetGraphViewModel(UObject asset)
    {
        var packageName = asset.Owner?.Name ?? asset.Name;
        var vm = new AnimGraphViewModel { PackageName = packageName };

        var layer = new AnimGraphLayer { Name = "Asset" };
        var node = new AnimGraphNode
        {
            Name = asset.Name,
            ExportType = asset.ExportType,
            NodePosX = 60,
            NodePosY = 60
        };

        node.AdditionalProperties["AssetName"] = asset.Name;
        node.AdditionalProperties["ExportType"] = asset.ExportType;
        if (!string.IsNullOrEmpty(asset.Owner?.Name))
            node.AdditionalProperties["Package"] = asset.Owner.Name;

        layer.Nodes.Add(node);
        vm.Nodes.Add(node);
        vm.Layers.Add(layer);
        return vm;
    }
}
