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
    public async Task CreateLoliBackup()
    {
        await _threadWorkerView.Begin(_ =>
        {
            var backupFolder = Path.Combine(UserSettings.Default.OutputDirectory, "Backups");
            Directory.CreateDirectory(backupFolder);
            var fileName = $"{Provider.ProjectName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.loli";
            var fullPath = Path.Combine(backupFolder, fileName);

            var entries = new List<BackupManagerViewModel.LoliEntry>();
            
            // UnloadedVfs
            foreach (var vfs in Provider.UnloadedVfs)
            {
                var fileCount = vfs.FileCount;
                if (vfs is IoStoreReader reader && reader.TocResource != null)
                    fileCount = (int) reader.TocResource.Header.TocEntryCount;
                entries.Add(new BackupManagerViewModel.LoliEntry(vfs.Name, vfs.EncryptionKeyGuid.ToString(), vfs.Length, fileCount));
            }
            // MountedVfs
            foreach (var vfs in Provider.MountedVfs)
            {
                var fileCount = vfs.FileCount;
                if (vfs is IoStoreReader reader && reader.TocResource != null)
                    fileCount = (int) reader.TocResource.Header.TocEntryCount;
                entries.Add(new BackupManagerViewModel.LoliEntry(vfs.Name, vfs.EncryptionKeyGuid.ToString(), vfs.Length, fileCount));
            }

            var json = JsonConvert.SerializeObject(entries, Formatting.Indented);
            {
                using var fileStream = new FileStream(fullPath, FileMode.Create);
                using var compressedStream = LZ4Stream.Encode(fileStream, LZ4Level.L00_FAST);
                using var writer = new StreamWriter(compressedStream);
                writer.Write(json);
            }

            SaveCheck(fullPath, fileName, "created", "create");
        });
    }

    private void ComparePak()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Loli Backup Files (*.loli)|*.loli",
            Title = Application.Current.FindResource("PakComparer_Dialog_Title") as string
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            string json;
            try
            {
                using var fileStream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read);
                using var decompressedStream = LZ4Stream.Decode(fileStream);
                using var reader = new StreamReader(decompressedStream);
                json = reader.ReadToEnd();
            }
            catch
            {
                json = File.ReadAllText(dialog.FileName);
            }
            var oldPaks = JsonConvert.DeserializeObject<List<BackupManagerViewModel.LoliEntry>>(json);
            
            // 現在のPakリスト (Unloaded + Mounted)
            var currentPaks = Provider.UnloadedVfs
                .Concat(Provider.MountedVfs)
                .GroupBy(x => x.EncryptionKeyGuid)
                .ToDictionary(x => x.Key, x => x.First());

            var diffs = new List<PakDiff>();

            foreach (var old in oldPaks)
            {
                var oldGuid = new FGuid(old.Guid.Replace("-", ""));
                if (currentPaks.TryGetValue(oldGuid, out var current))
                {
                    var currentFileCount = current.FileCount;
                    if (current is IoStoreReader reader && reader.TocResource != null)
                        currentFileCount = (int) reader.TocResource.Header.TocEntryCount;
                    Log.Information("[Compare] Archive: {Name} | GUID: {Guid} | OldFiles: {OldCount} | NewFiles: {NewCount}", current.Name, current.EncryptionKeyGuid, old.FileCount, currentFileCount);

                    if (!string.Equals(old.Name, current.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        diffs.Add(new PakDiff
                        {
                            Name = $"{old.Name} -> {current.Name}",
                            Guid = current.EncryptionKeyGuid.ToString(),
                            Status = Application.Current.FindResource("PakComparer_Status_Renamed") as string,
                            StatusType = EPakDiffStatus.Renamed,
                            SizeDiff = current.Length - old.Length,
                            CountDiff = currentFileCount - old.FileCount,
                            OldSize = old.Length,
                            NewSize = current.Length,
                            OldCount = old.FileCount,
                            NewCount = currentFileCount
                        });
                    }
                    else if (current.Length != old.Length || currentFileCount != old.FileCount)
                    {
                        diffs.Add(new PakDiff
                        {
                            Name = current.Name,
                            Guid = current.EncryptionKeyGuid.ToString(),
                            Status = Application.Current.FindResource("PakComparer_Status_Modified") as string,
                            StatusType = EPakDiffStatus.Modified,
                            SizeDiff = current.Length - old.Length,
                            CountDiff = currentFileCount - old.FileCount,
                            OldSize = old.Length,
                            NewSize = current.Length,
                            OldCount = old.FileCount,
                            NewCount = currentFileCount
                        });
                    }
                    currentPaks.Remove(oldGuid);
                }
                else
                {
                    diffs.Add(new PakDiff
                    {
                        Name = old.Name,
                        Guid = old.Guid,
                        Status = Application.Current.FindResource("PakComparer_Status_Removed") as string,
                        StatusType = EPakDiffStatus.Removed,
                        SizeDiff = -old.Length,
                        CountDiff = -old.FileCount,
                        OldSize = old.Length,
                        NewSize = 0,
                        OldCount = old.FileCount,
                        NewCount = 0
                    });
                }
            }

            foreach (var current in currentPaks.Values)
            {
                var currentFileCount = current.FileCount;
                if (current is IoStoreReader reader && reader.TocResource != null)
                    currentFileCount = (int) reader.TocResource.Header.TocEntryCount;

                diffs.Add(new PakDiff
                {
                    Name = current.Name,
                    Guid = current.EncryptionKeyGuid.ToString(),
                    Status = Application.Current.FindResource("PakComparer_Status_New") as string,
                    StatusType = EPakDiffStatus.New,
                    SizeDiff = current.Length,
                    CountDiff = currentFileCount,
                    OldSize = 0,
                    NewSize = current.Length,
                    OldCount = 0,
                    NewCount = currentFileCount
                });
            }

            new PakComparerWindow(diffs).Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Application.Current.FindResource("PakComparer_Error_Message") as string, ex.Message), Application.Current.FindResource("PakComparer_Error_Title") as string, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveCheck(string fullPath, string fileName, string type1, string type2)
    {
        if (new FileInfo(fullPath).Length > 0)
        {
            Log.Information("{FileName} successfully {Type}", fileName, type1);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text($"Successfully {type1} ", Constants.WHITE);
                FLogger.Link(fileName, fullPath, true);
            });
        }
        else
        {
            Log.Error("{FileName} could not be {Type}", fileName, type1);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not {type2} '{fileName}'", Constants.WHITE, true));
        }
    }

    public class PakDiff
    {
        public string Name { get; set; }
        public string Guid { get; set; }
        public string Status { get; set; }
        public EPakDiffStatus StatusType { get; set; }
        public long SizeDiff { get; set; }
        public int CountDiff { get; set; }
        public long OldSize { get; set; }
        public long NewSize { get; set; }
        public int OldCount { get; set; }
        public int NewCount { get; set; }
    }

    public enum EPakDiffStatus
    {
        New,
        Modified,
        Removed,
        Renamed
    }
}
