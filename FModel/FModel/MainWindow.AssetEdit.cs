using System;
using System.Collections.Generic; // List<> を使用するために追加
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdonisUI.Controls;
using UAssetAPI;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using CUE4Parse.UE4.Assets;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Exceptions;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using FModel.Views;
using FModel.Views.Resources.Controls;
using ICSharpCode.AvalonEdit.Editing;
using FModel.Framework; // RelayCommand を使用するためのやつ
using System.Collections.Specialized; // NotifyCollectionChangedEventArgs を使用するためのやつ
using Microsoft.Win32;
using FModel.Features.Athena;
using Serilog;
using Ookii.Dialogs.Wpf;
using UAssetAPI.PropertyTypes.Structs;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using Newtonsoft.Json;

namespace FModel;

public partial class MainWindow
{
    private void OnTestEditAssetClick(object sender, RoutedEventArgs e)
    {
        // 1. ファイルリストから選択中のアセットを取得
        if (AssetsListName.SelectedItem is not GameFile selectedAsset)
        {
            MessageBox.Show("Please select an asset from the file list first.", "No Asset Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2. CUE4Parseからアセットデータをバイト配列として取得
        var provider = _applicationView.CUE4Parse.Provider;
        // ISavable, CanSaveは存在しないため、providerがnullかどうかのみ判定
        if (provider is null)
        {
            MessageBox.Show("The file provider is not ready.", "Provider Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ★★★ 編集ダイアログを表示 ★★★
        var dialog = new PropertyEditDialog();
        if (dialog.ShowDialog() != true)
        {
            return; // ユーザーがキャンセルした
        }

        // 一時ディレクトリを作成
        var tempDir = Path.Combine(Path.GetTempPath(), $"FModel_Edit_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 3. アセットを一時ファイルとしてディスクに書き出す (UAssetAPIはファイルパスから読み込むため)
            if (!provider.TryGetGameFile(selectedAsset.Path, out var selectedGameFile))
            {
                MessageBox.Show("Failed to extract asset data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var data = selectedGameFile.Read();
            if (data == null || data.Length == 0)
            {
                MessageBox.Show("Failed to extract asset data.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var tempAssetPath = Path.Combine(tempDir, selectedAsset.Name);
            File.WriteAllBytes(tempAssetPath, data);

            // .uexp ファイルも存在する場合は一緒に書き出す
            var uexpPath = Path.ChangeExtension(selectedAsset.Path, ".uexp");
            // FileExistsは存在しないため、TryGetGameFileで存在確認
            if (provider.TryGetGameFile(uexpPath, out var uexpGameFile))
            {
                var uexpData = uexpGameFile.Read();
                var tempUexpPath = Path.Combine(tempDir, Path.GetFileName(uexpPath));
                File.WriteAllBytes(tempUexpPath, uexpData);
            }

            // 4. 編集後のアセットの保存先をユーザーに選択させる
            var saveFileDialog = new SaveFileDialog
            {
                FileName = $"{Path.GetFileNameWithoutExtension(selectedAsset.Name)}_Edited.uasset",
                Filter = "Unreal Asset (*.uasset)|*.uasset",
                Title = "Save Edited Asset As"
            };

            if (saveFileDialog.ShowDialog() != true)
            {
                return; // ユーザーがキャンセルした
            }
            var destinationPath = saveFileDialog.FileName;

            // 5. AssetEditor.EditAndSave を使ってアセットを編集・保存
            // UE4VersionではなくEngineVersion（UAssetAPI.EngineVersion）を使用
            var engineVersion = EngineVersion.VER_UE4_27; // 自動取得ができないため固定値を指定

            AssetEditor.EditAndSave(tempAssetPath, destinationPath, engineVersion, asset =>
            {
                // --- ここからがアセットの編集ロジック ---
                Log.Information($"Attempting to edit asset: {selectedAsset.Name}");
                bool modified = false;

                // DataTableアセットの場合、最初の行を対象とする
                if (asset.Exports.Count > 0 && asset.Exports[0] is DataTableExport dataTableExport && dataTableExport.Table.Data.Count > 0)
                {
                    var firstRow = dataTableExport.Table.Data[0];
                    var property = firstRow.Value.FirstOrDefault(x => x.Name.ToString() == dialog.PropertyName);
                    if (property != null)
                    {
                        modified = TrySetPropertyValue(property, dialog.PropertyType, dialog.PropertyValue, dialog.EnumType, asset);
                    }
                    else
                    {
                        Log.Warning($"Property '{dialog.PropertyName}' not found in the first row of the DataTable.");
                    }
                }
                // 通常のBlueprintアセットなどの場合
                else if (asset.Exports.Count > 0 && asset.Exports[0] is NormalExport normalExport)
                {
                    var property = normalExport.Data.FirstOrDefault(x => x.Name.ToString() == dialog.PropertyName);
                    if (property != null)
                    {
                        modified = TrySetPropertyValue(property, dialog.PropertyType, dialog.PropertyValue, dialog.EnumType, asset);
                    }
                    else
                    {
                        Log.Warning($"Property '{dialog.PropertyName}' not found in the export.");
                    }
                }

                if (!modified)
                {
                    Log.Warning("Failed to modify the property. The asset will be saved without changes.");
                }
                // --- 編集ロジックここまで ---
            });

            MessageBox.Show($"Asset successfully edited and saved to:\n{destinationPath}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to edit and save asset.");
            MessageBox.Show($"An error occurred while editing the asset:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 6. 一時ファイルをクリーンアップ
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private bool TrySetPropertyValue(PropertyData property, string targetType, string stringValue, string enumType, UAsset asset)
    {
        try
        {
            if (property.GetType().Name != targetType)
            {
                Log.Warning($"Property '{property.Name}' was found, but its type '{property.GetType().Name}' did not match the selected type '{targetType}'.");
                return false;
            }

            switch (targetType)
            {
                case "TextPropertyData":
                    if (property is TextPropertyData textProp) { textProp.Value = new UAssetAPI.UnrealTypes.FString(stringValue); }
                    break;
                case "IntPropertyData":
                    if (property is IntPropertyData intProp && int.TryParse(stringValue, out var intVal)) { intProp.Value = intVal; }
                    break;
                case "FloatPropertyData":
                    if (property is FloatPropertyData floatProp && float.TryParse(stringValue, out var floatVal)) { floatProp.Value = floatVal; }
                    break;
                case "NamePropertyData":
                    if (property is NamePropertyData nameProp) { nameProp.Value = FName.FromString(asset, stringValue); }
                    break;
                case "BoolPropertyData":
                    if (property is BoolPropertyData boolProp && bool.TryParse(stringValue, out var boolVal)) { boolProp.Value = boolVal; }
                    break;
                case "BytePropertyData":
                    if (property is BytePropertyData byteProp)
                    {
                        if (!string.IsNullOrWhiteSpace(enumType))
                        {
                            byteProp.EnumType = FName.FromString(asset, enumType);
                            // byteProp.Value = FName.FromString(asset, stringValue);
                        }
                        else if (byte.TryParse(stringValue, out var byteVal))
                        {
                            byteProp.ByteType = BytePropertyType.Byte;
                            var enumValueField = typeof(BytePropertyData).GetField("EnumValue", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (enumValueField != null)
                            {
                                enumValueField.SetValue(byteProp, byteVal);
                                byteProp.Value = byteVal;
                            }
                            else { return false; }
                        }
                        else { return false; }
                    }
                    break;
                default:
                    return false;
            }

            Log.Information($"Successfully set {targetType.Replace("Data", "")} '{property.Name}' to '{stringValue}'.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Error setting property value for '{property.Name}'.");
            return false;
        }
    }
}
