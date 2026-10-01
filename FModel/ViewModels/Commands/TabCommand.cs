using System;
using System.Windows;
using AdonisUI.Controls;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Views.Resources.Controls;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace FModel.ViewModels.Commands;

public class TabCommand : ViewModelCommand<TabItem>
{
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;

    public TabCommand(TabItem contextViewModel) : base(contextViewModel)
    {
    }

    public override async void Execute(TabItem tabViewModel, object parameter)
    {
        switch (parameter)
        {
            case TabItem mdlClick:
                _applicationView.CUE4Parse.TabControl.RemoveTab(mdlClick);
                break;
            case "Close_Tab":
                _applicationView.CUE4Parse.TabControl.RemoveTab(tabViewModel);
                break;
            case "Close_All_Tabs":
                _applicationView.CUE4Parse.TabControl.RemoveAllTabs();
                break;
            case "Close_Other_Tabs":
                _applicationView.CUE4Parse.TabControl.RemoveOtherTabs(tabViewModel);
                break;
            case "Previous_Export_Page":
            case "Next_Export_Page":
            {
                var exportIndex = tabViewModel.GetExportPageStart(Equals(parameter, "Next_Export_Page") ? 1 : -1);
                if (exportIndex < 0) break;

                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.ExtractExportPage(cancellationToken, tabViewModel.Entry, exportIndex);
                });
                break;
            }
            case "Assets_Show_Metadata":
                _applicationView.CUE4Parse.ShowMetadata(tabViewModel.Entry);
                break;
            case "Find_References":
                _applicationView.CUE4Parse.FindReferences(tabViewModel.Entry);
                break;
            case "Assets_Decompile":
                _applicationView.CUE4Parse.Decompile(tabViewModel.Entry);
                break;
            case "Assets_Verse_Declarations":
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.RecoverVerseDeclarations(tabViewModel.Entry));
                break;
            case "Assets_Verse_Listing":
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.RecoverVerseDeclarations(tabViewModel.Entry, listing: true));
                break;
            case "Save_Data":
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.ExportData(tabViewModel.Entry));
                break;
            case "Save_Properties":
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.Extract(cancellationToken, tabViewModel.Entry, false, EBulkType.Properties);
                });
                break;
            case "Save_Textures":
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.Extract(cancellationToken, tabViewModel.Entry, false, EBulkType.Textures);
                });
                break;
            case "Save_Models":
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.Extract(cancellationToken, tabViewModel.Entry, false, EBulkType.Meshes);
                });
                break;
            case "Save_Worlds":
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.Extract(cancellationToken, tabViewModel.Entry, false, EBulkType.Worlds);
                });
                break;
            case "Save_Animations":
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.Extract(cancellationToken, tabViewModel.Entry, false, EBulkType.Animations);
                });
                break;
            case "Save_Audio":
                await _threadWorkerView.Begin(cancellationToken =>
                {
                    _applicationView.CUE4Parse.Extract(cancellationToken, tabViewModel.Entry, false, EBulkType.Audio);
                });
                break;
            case "Open_Properties":
                if (tabViewModel.Header == "New Tab" || tabViewModel.Document == null) return;
                Helper.OpenWindow<AdonisWindow>(tabViewModel.Header + " (Properties)", () =>
                {
                    new PropertiesPopout(tabViewModel)
                    {
                        Title = tabViewModel.Header + " (Properties)"
                    }.Show();
                });
                break;
            case "File_Path":
                Clipboard.SetText(tabViewModel.Entry.Path);
                break;
            case "File_Name":
                Clipboard.SetText(tabViewModel.Entry.Name);
                break;
            case "Directory_Path":
                Clipboard.SetText(tabViewModel.Entry.Directory);
                break;
            case "File_Path_No_Extension":
                Clipboard.SetText(tabViewModel.Entry.PathWithoutExtension);
                break;
            case "File_Name_No_Extension":
                Clipboard.SetText(tabViewModel.Entry.NameWithoutExtension);
                break;
            case "Object_Path":
                Clipboard.SetText(_applicationView.CUE4Parse.Provider.GetObjectPath(tabViewModel.Entry));
                break;
            case "Asset_Edit_Json":
                if (!tabViewModel.BeginJsonEdit())
                {
                    MessageBox.Show("JSON を表示しているパッケージ（.uasset / .umap）のタブで実行してください。", "JSON 編集",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                break;
            case "Asset_Replace_Texture":
            {
                if (!tabViewModel.BeginJsonEdit())
                {
                    MessageBox.Show("テクスチャのパッケージ（.uasset）を開いたタブで実行してください。", "テクスチャの差し替え",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }

                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "差し替える画像を選択（元のテクスチャと同じ解像度に合わせて書き込みます）",
                    Filter = "画像 (*.png;*.jpg;*.jpeg;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.webp|すべてのファイル (*.*)|*.*",
                    Multiselect = false
                };
                if (dialog.ShowDialog() != true) break;

                tabViewModel.ReplacementImagePath = dialog.FileName;
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.SaveEditedJson(tabViewModel));
                break;
            }
            case "Asset_Export_Mesh_Obj":
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.ExportMeshForEditing(tabViewModel.Entry));
                break;
            case "Asset_Replace_Mesh":
            {
                if (!tabViewModel.BeginJsonEdit())
                {
                    MessageBox.Show("メッシュのパッケージ（.uasset）を開いたタブで実行してください。", "メッシュの差し替え",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }

                var editDirectory = CUE4ParseViewModel.MeshEditPath(tabViewModel.Entry);
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "編集した OBJ を選択（「メッシュを編集用 OBJ に書き出す」で書き出して変形したもの）",
                    Filter = "OBJ (*.obj)|*.obj",
                    InitialDirectory = System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(editDirectory)) ? System.IO.Path.GetDirectoryName(editDirectory) : null,
                    Multiselect = false
                };
                if (dialog.ShowDialog() != true) break;

                tabViewModel.ReplacementMeshPath = dialog.FileName;
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.SaveEditedJson(tabViewModel));
                break;
            }
            case "Asset_Clear_Replacements":
                tabViewModel.ReplacementImagePath = null;
                tabViewModel.ReplacementMeshPath = null;
                break;
            case "Asset_Save_Edited_Json":
                if (!tabViewModel.IsEditingJson) break;
                await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.SaveEditedJson(tabViewModel));
                break;
            case "Asset_Stop_Json_Edit":
                if (tabViewModel.HasJsonEdits && MessageBox.Show("編集内容を破棄して元の JSON 表示に戻しますか？\n（書き出し済みの .uasset はそのまま残ります）", "JSON 編集",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    break;
                tabViewModel.EndJsonEdit(true);
                break;
            case "Asset_Create_Pak":
                Helper.OpenWindow<AdonisWindow>("Pak Creator", () => new Views.PakCreatorWindow().Show());
                break;
            case "Asset_Open_Edited_Directory":
                CUE4ParseViewModel.OpenEditedAssetsDirectory(tabViewModel.Entry);
                break;
        }

        if (parameter is string command && command.StartsWith("Save_", StringComparison.Ordinal)) // This is kinda bad
        {
            await ExportSessionViewModel.Instance.ExportAutomaticallyAsync();
        }
    }
}
