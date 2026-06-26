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
    /// <summary>
    /// SVGデータを SKBitmap で描画して返す簡易メソッド
    /// </summary>
    public SkiaSharp.SKBitmap RenderSvg(byte[] svgData, int width = 512, int height = 512)
    {
        using var stream = new System.IO.MemoryStream(svgData);
        var svg = new SKSvg();
        if (svg.Load(stream) == null)
            return null;
        
        var bitmap = new SkiaSharp.SKBitmap(width, height);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            if (svg.Picture != null)
            {
                var scaleX = width / svg.Picture.CullRect.Width;
                var scaleY = height / svg.Picture.CullRect.Height;
                var scale = Math.Min(scaleX, scaleY);
                
                var matrix = SkiaSharp.SKMatrix.CreateScale(scale, scale);
                canvas.DrawPicture(svg.Picture, ref matrix);
            }
        }
        return bitmap;
    }

    /// <summary>
    /// DiffProvider の GameDirectory を返すプロパティ
    /// </summary>
    public GameDirectoryViewModel DiffGameDirectory => DiffProvider != null ? new GameDirectoryViewModel() : null;

    private bool _modelIsOverwritingMaterial;
    public bool ModelIsOverwritingMaterial
    {
        get => _modelIsOverwritingMaterial;
        set => SetProperty(ref _modelIsOverwritingMaterial, value);
    }

    private bool _modelIsWaitingAnimation;
    public bool ModelIsWaitingAnimation
    {
        get => _modelIsWaitingAnimation;
        set => SetProperty(ref _modelIsWaitingAnimation, value);
    }

    public bool IsSnooperOpen => _snooper is { Exists: true, IsVisible: true };
    private Snooper _snooper;
    public Snooper SnooperViewer
    {
        get
        {
            if (_snooper != null) return _snooper;

            return Application.Current.Dispatcher.Invoke(delegate
            {
                var scale = ImGuiController.GetDpiScale();
                var htz = Snooper.GetMaxRefreshFrequency();
                return _snooper = new Snooper(
                    new GameWindowSettings { UpdateFrequency = htz },
                    new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenWidth * .75 * scale),
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenHeight * .85 * scale)),
                        NumberOfSamples = Constants.SAMPLES_COUNT,
                        WindowBorder = WindowBorder.Resizable,
                        Flags = ContextFlags.ForwardCompatible,
                        Profile = ContextProfile.Core,
                        Vsync = VSyncMode.Adaptive,
                        APIVersion = new Version(4, 6),
                        StartVisible = false,
                        StartFocused = false,
                        Title = "3D Viewer"
                    });
            });
        }
    }
}
