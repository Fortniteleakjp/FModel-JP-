using System;
using System.Collections.Generic;
using System.Diagnostics;
using CUE4Parse.UE4.Objects.Core.Misc;
using FModel.Framework;
using ImGuiNET;
using OpenTK.Windowing.Common;
using System.Numerics;
using System.Text;
using FModel.Settings;
using FModel.Views.Snooper.Animations;
using FModel.Views.Snooper.Models;
using FModel.Views.Snooper.Shading;
using ImGuizmoNET;
using OpenTK.Graphics.OpenGL4;

namespace FModel.Views.Snooper;

public class Swap
{
    public string Title;
    public string Description;
    public bool Value;
    public bool IsAware;
    public Action Content;

    public Swap()
    {
        Reset();
    }

    public void Reset()
    {
        Title = string.Empty;
        Description = string.Empty;
        Value = false;
        Content = null;
    }
}

public class Save
{
    public bool Value;
    public string Label;
    public string Path;

    public Save()
    {
        Reset();
    }

    public void Reset()
    {
        Value = false;
        Label = string.Empty;
        Path = string.Empty;
    }
}

public class SnimGui
{
    public readonly ImGuiController Controller;
    private readonly Swap _swapper = new ();
    private readonly Save _saver = new ();
    private readonly string _renderer;
    private readonly string _version;
    private readonly float _tableWidth;

    private Vector2 _outlinerSize;
    private bool _tiOpen;
    private bool _transformOpen;
    private bool _viewportFocus;
    private bool _viewportPanning; // JP操作性: 中ボタンドラッグでのパン中フラグ
    private bool _addModelOpen;     // JP機能: 「モデル追加」モーダルを開くトリガー
    private string _addModelPaths = ""; // JP機能: 追加するアセットのフルパス入力(複数行)
    private string _addModelResult = ""; // JP機能: 追加結果メッセージ
    private readonly Stack<Action> _undoStack = new(); // JP機能: Ctrl+Z 用の取り消しスタック(各要素=一手前へ戻す処理)
    private bool _gizmoWasUsing;        // JP機能: ギズモ操作のドラッグ開始検出用
    private OPERATION _guizmoOperation;

    private readonly Vector4 _accentColor = new (0.125f, 0.42f, 0.831f, 1.0f);
    private readonly Vector4 _alertColor = new (0.831f, 0.573f, 0.125f, 1.0f);
    private readonly Vector4 _errorColor = new (0.761f, 0.169f, 0.169f, 1.0f);

    private const uint _dockspaceId = 1337;

    public SnimGui(int width, int height)
    {
        Controller = new ImGuiController(width, height);

        _renderer = GL.GetString(StringName.Renderer);
        _version = "OpenGL " + GL.GetString(StringName.Version);
        _tableWidth = 17 * Controller.DpiScale;
        _guizmoOperation = OPERATION.UNIVERSAL; // JP: 移動+回転+拡縮を一体表示するギズモを既定に

        Theme();
    }

    public void Render(Snooper s)
    {
        // JP機能: Ctrl+Z で一つ前の状態に戻す(主にギズモによる移動/回転/拡縮の取り消し)。
        // テキスト入力中は入力欄側のCtrl+Zを優先するため除外。
        var undoIo = ImGui.GetIO();
        if (!undoIo.WantTextInput && undoIo.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.Z, false) && _undoStack.Count > 0)
        {
            var undo = _undoStack.Pop();
            try { undo(); } catch { /* 取り消し対象が既に存在しない等は無視 */ }
        }

        ImGui.DockSpaceOverViewport(_dockspaceId, ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode);

        SectionWindow("Material Inspector", s.Renderer, DrawMaterialInspector, false);
        AnimationWindow("Timeline", s.Renderer, (icons, tracker, animations) =>
            tracker.ImGuiTimeline(s, _saver, icons, animations, _outlinerSize, Controller.FontSemiBold));

        Window("World", () => DrawWorld(s), false);

        DrawSockets(s);
        DrawOuliner(s);
        DrawDetails(s);
        Draw3DViewport(s);
        DrawNavbar();

        DrawTextureInspector(s);
        DrawSkeletonTree(s);

        DrawModals(s);

        Controller.Render();
    }

    private void DrawModals(Snooper s)
    {
        Modal(_swapper.Title, _swapper.Value, () =>
        {
            ImGui.TextWrapped(_swapper.Description);
            ImGui.Separator();

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
            ImGui.Checkbox("了解！次回から表示しない", ref _swapper.IsAware);
            ImGui.PopStyleVar();

            var size = new Vector2(120, 0);
            if (ImGui.Button("OK", size))
            {
                _swapper.Content();
                _swapper.Reset();
                ImGui.CloseCurrentPopup();
                s.WindowShouldClose(true, false);
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();

            if (ImGui.Button("キャンセル", size))
            {
                _swapper.Reset();
                ImGui.CloseCurrentPopup();
            }
        });

        Modal("保存しました",_saver.Value, () =>
        {
            ImGui.TextWrapped($"{_saver.Label} を保存しました");
            ImGui.Separator();

            var size = new Vector2(120, 0);
            if (ImGui.Button("OK", size))
            {
                _saver.Reset();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();

            if (ImGui.Button("エクスプローラーで表示", size))
            {
                Process.Start("explorer.exe", $"/select, \"{_saver.Path.Replace('/', '\\')}\"");

                _saver.Reset();
                ImGui.CloseCurrentPopup();
            }
        });

        // JP機能: パス指定で別モデルを現在のシーンに追加するモーダル
        // OpenPopup と BeginPopupModal を同一スコープ(ここ)に置きIDを一致させる。トリガーは _addModelOpen フラグ。
        if (_addModelOpen) { ImGui.OpenPopup("モデルを追加###add_model"); _addModelOpen = false; }
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(560 * Controller.DpiScale, 0), ImGuiCond.Appearing);
        var addOpen = true;
        if (ImGui.BeginPopupModal("モデルを追加###add_model", ref addOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("追加したいアセットのフルパスを入力してください（複数可・1行に1つ）。");
            ImGui.TextDisabled("拡張子なし、または .オブジェクト名付き。例: FortniteGame/Content/.../SK_Mannequin.SK_Mannequin");
            ImGui.TextDisabled("貼り付けは Ctrl+V または下の「貼り付け」ボタン。");
            ImGui.InputTextMultiline("##add_model_paths", ref _addModelPaths, 16384, new Vector2(540 * Controller.DpiScale, 160 * Controller.DpiScale));

            // JP: Ctrl+V が効かない環境向けの確実な貼り付けボタン(OSクリップボードを末尾に追記)
            if (ImGui.Button("貼り付け"))
            {
                var clip = Controller.GetClipboardText();
                if (!string.IsNullOrEmpty(clip))
                {
                    if (_addModelPaths.Length > 0 && !_addModelPaths.EndsWith('\n')) _addModelPaths += '\n';
                    _addModelPaths += clip;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("クリア")) _addModelPaths = "";

            if (_addModelResult.Length > 0)
            {
                ImGui.Spacing();
                ImGui.TextWrapped(_addModelResult);
            }

            ImGui.Separator();
            if (ImGui.Button("追加", new Vector2(120, 0)))
            {
                var (added, failed) = s.Renderer.LoadModelsByPaths(_addModelPaths);
                _addModelResult = $"{added} 件をシーンに追加しました。";
                if (failed.Count > 0)
                    _addModelResult += $"\n失敗 {failed.Count} 件（見つからない/非対応）:\n" + string.Join("\n", failed);
                else
                    _addModelPaths = "";
            }
            ImGui.SameLine();
            if (ImGui.Button("閉じる", new Vector2(120, 0)))
            {
                _addModelResult = "";
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void DrawWorld(Snooper s)
    {
        if (ImGui.BeginTable("world_details", 2, ImGuiTableFlags.SizingStretchProp))
        {
            var b = false;
            var length = s.Renderer.Options.Models.Count;

            NoFramePaddingOnY(() =>
            {
                Layout("レンダラー");ImGui.Text($" :  {_renderer}");
                Layout("バージョン");ImGui.Text($" :  {_version}");
                Layout("読み込み済みモデル");ImGui.Text($" :  x{length}");ImGui.SameLine();

                if (ImGui.SmallButton("すべて保存"))
                {
                    foreach (var model in s.Renderer.Options.Models.Values)
                    {
                        b |= model.Save(out _, out _);
                    }
                }
            });

            Modal("保存しました",b, () =>
            {
                ImGui.TextWrapped($"{length} 個のモデルを保存しました");
                ImGui.Separator();

                var size = new Vector2(120, 0);
                if (ImGui.Button("OK", size))
                {
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SetItemDefaultFocus();
                ImGui.SameLine();

                if (ImGui.Button("エクスプローラーで表示", size))
                {
                    Process.Start("explorer.exe", $"/select, \"{UserSettings.Default.ModelDirectory.Replace('/', '\\')}\"");
                    ImGui.CloseCurrentPopup();
                }
            });

            ImGui.EndTable();
        }

        // JP機能: パス指定で現在のシーンにモデルを追加
        if (ImGui.Button("＋ モデルを追加 (パス指定)", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
        {
            _addModelOpen = true;
            _addModelResult = "";
        }

        ImGui.SeparatorText("エディタ");
        if (ImGui.BeginTable("world_editor", 2))
        {
            Layout("回転のみでアニメ");ImGui.PushID(1);
            ImGui.Checkbox("", ref s.Renderer.AnimateWithRotationOnly);
            ImGui.PopID();Layout("再生速度");ImGui.PushID(2);
            ImGui.DragFloat("", ref s.Renderer.Options.Tracker.TimeMultiplier, 0.01f, 0.25f, 8f, "x%.2f", ImGuiSliderFlags.NoInput);
            ImGui.PopID();Layout("頂点カラー");ImGui.PushID(3);
            var c = (int) s.Renderer.Color;
            ImGui.Combo("vertex_colors", ref c,
                "デフォルト\0セクション\0カラー\0法線\0UV座標\0");
            s.Renderer.Color = (VertexColor) c;
            ImGui.PopID();

            ImGui.EndTable();
        }

        ImGui.SeparatorText("カメラ");
        s.Renderer.CameraOp.ImGuiCamera();

        ImGui.SeparatorText("ライト");
        for (int i = 0; i < s.Renderer.Options.Lights.Count; i++)
        {
            var light = s.Renderer.Options.Lights[i];
            var id = s.Renderer.Options.TryGetModel(light.Model, out var lightModel) ? lightModel.Name : "なし";

            id += $"##{i}";
            if (ImGui.TreeNode(id) && ImGui.BeginTable(id, 2))
            {
                s.Renderer.Options.SelectModel(light.Model);
                light.ImGuiLight();
                ImGui.EndTable();
                ImGui.TreePop();
            }
        }
    }

    private void DrawNavbar()
    {
        if (!ImGui.BeginMainMenuBar()) return;

        const int cursorX = 360;
        Modal("操作ガイド", ImGui.MenuItem("操作ガイド"), () =>
        {
            ImGui.TextWrapped(
                @"この3Dビューアでできる主な操作の一覧です（すべてではありません）:

1. UI / 操作
    - ウィンドウ移動中に Shift を押すとドッキング
    - 入力欄をダブルクリックで数値を直接入力
    - 入力欄をクリック＋ドラッグで（入力せずに）数値を増減
    - H でウィンドウを隠し、次に抽出したメッシュを追加表示

2. ビューポート
    - WASD で移動
    - E / Q で上下移動
    - Shift で高速移動
    - マウスホイールでズーム（前後ドリー）
    - 中ボタン＋ドラッグでパン
    - F で選択モデルにフォーカス（フレーミング）
    - X / C で視野角（FOV）を変更
    - Z で選択モデルをアニメーション
    - 左ボタン押下で視点回転（見回す）
    - 右クリックでワールド内のモデルを選択

3. アウトライナー
    3.1. モデルを右クリック
        - 表示 / 非表示
        - スケルタル（骨格）表示
        - .psk / .pskx で保存
        - アニメーションを読み込む
        - そのモデルの位置へカメラを移動
        - 削除
        - 選択解除
        - パスをコピー

4. ワールド
    - すべて保存：読み込み済みモデルを一括保存
      （フリーズしても故障ではありません。保存中なだけです）

5. 詳細
    5.1. セクションを右クリック
        - セクションの表示 / 非表示
        - 入れ替え：このセクションのマテリアルを変更
        - パスをコピー
    5.2. トランスフォーム
        - ワールド内でモデルを 移動 / 回転 / 拡大縮小
    5.3. モーフターゲット
        - 頂点位置を指定量だけ変化させてモデルの形状を変える

6. タイムライン
    - Space で再生 / 一時停止
    - マウスで時間を操作
    6.1 右クリック
        - 別の読み込み済みモデルにアニメーションを適用
        - 保存
        - パスをコピー
");
            ImGui.Separator();

            ImGui.SetCursorPosX(cursorX);
            ImGui.SetItemDefaultFocus();
            if (ImGui.Button("OK", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        });

        Modal("GPU / OpenGL 情報", ImGui.MenuItem("GPU情報"), () =>
        {
            var s = new StringBuilder();
            s.AppendLine($"MaxTextureImageUnits: {GL.GetInteger(GetPName.MaxTextureImageUnits)}");
            s.AppendLine($"MaxTextureUnits: {GL.GetInteger(GetPName.MaxTextureUnits)}");
            s.AppendLine($"MaxVertexTextureImageUnits: {GL.GetInteger(GetPName.MaxVertexTextureImageUnits)}");
            s.AppendLine($"MaxCombinedTextureImageUnits: {GL.GetInteger(GetPName.MaxCombinedTextureImageUnits)}");
            s.AppendLine($"MaxGeometryTextureImageUnits: {GL.GetInteger(GetPName.MaxGeometryTextureImageUnits)}");
            s.AppendLine($"MaxTextureCoords: {GL.GetInteger(GetPName.MaxTextureCoords)}");
            s.AppendLine($"Renderer: {_renderer}");
            s.AppendLine($"Version: {_version}");
            ImGui.TextWrapped(s.ToString());
            ImGui.Separator();

            ImGui.SetCursorPosX(cursorX);
            ImGui.SetItemDefaultFocus();
            if (ImGui.Button("OK", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        });

        Modal("Snooper について", ImGui.MenuItem("情報"), () =>
        {
            ImGui.TextWrapped(
                @"Snooper は ""OpenGL x ImGui"" ベースの3Dビューアで、前世代を改善しデータマイニングの可能性を広げるために数か月をかけて作られました。これまで FModel を含む多くのソフトはエンドユーザーに最小限の情報しか見せてきませんでした。これは FModel を、Unreal Engine とその構造を深く掘り下げ、内部の仕組みを示せる実用的なオープンソースツールにするための、長く険しい移行の第一歩です。

Snooper は、ほとんどの UE 製ゲームと互換性を保ちつつ、モデル・マテリアル・スケルタルアニメーション・パーティクル・レベル・レベルアニメーションを正確にプレビューすることを目指しています。決して簡単な仕事ではなく、すべてが実現できるか分かりませんが、FModel の未来に向けたアイデアとビジョンを我々は持っています。
");
            ImGui.Separator();

            ImGui.SetCursorPosX(cursorX);
            ImGui.SetItemDefaultFocus();
            if (ImGui.Button("OK", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
        });

        // JP機能: パス指定で現在のシーンにモデルを追加
        if (ImGui.MenuItem("＋ モデル追加")) { _addModelOpen = true; _addModelResult = ""; }

        const string text = "H で隠す / ESC で終了...";
        ImGui.SetCursorPosX(ImGui.GetWindowViewport().WorkSize.X - ImGui.CalcTextSize(text).X - 5);
        ImGui.TextColored(new Vector4(0.36f, 0.42f, 0.47f, 1.00f), text);

        ImGui.EndMainMenuBar();
    }

    private void DrawOuliner(Snooper s)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        Window("Outliner", () =>
        {
            _outlinerSize = ImGui.GetWindowSize();
            if (ImGui.BeginTable("Items", 4, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersOuterV | ImGuiTableFlags.NoSavedSettings, ImGui.GetContentRegionAvail()))
            {
                ImGui.TableSetupColumn("数", ImGuiTableColumnFlags.NoHeaderWidth | ImGuiTableColumnFlags.WidthFixed, _tableWidth);
                ImGui.TableSetupColumn("UV", ImGuiTableColumnFlags.NoHeaderWidth | ImGuiTableColumnFlags.WidthFixed, _tableWidth);
                ImGui.TableSetupColumn("名前", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.NoHeaderWidth | ImGuiTableColumnFlags.WidthFixed, _tableWidth);
                ImGui.TableHeadersRow();

                var i = 0;
                foreach ((var guid, var model) in s.Renderer.Options.Models)
                {
                    ImGui.PushID(i);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (!model.IsVisible)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(1, 0, 0, .5f)));
                    else if (model.Attachments.IsAttachment)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0, .75f, 0, .5f)));
                    else if (model.Attachments.IsAttached)
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(1, 1, 0, .5f)));

                    ImGui.Text(model.TransformsCount.ToString("D"));
                    ImGui.TableNextColumn();
                    ImGui.Text(model.UvCount.ToString("D"));
                    ImGui.TableNextColumn();
                    var doubleClick = false;
                    if (ImGui.Selectable(model.Name, s.Renderer.Options.SelectedModel == guid, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        s.Renderer.Options.SelectModel(guid);
                        doubleClick = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
                    }
                    Popup(() =>
                    {
                        s.Renderer.Options.SelectModel(guid);
                        if (ImGui.MenuItem("表示", null, model.IsVisible)) model.IsVisible = !model.IsVisible;
                        if (ImGui.MenuItem("ワイヤーフレーム", null, model.ShowWireframe)) model.ShowWireframe = !model.ShowWireframe;
                        if (ImGui.MenuItem("コリジョン", null, model.ShowCollisions, model.HasCollisions)) model.ShowCollisions = !model.ShowCollisions;
                        ImGui.Separator();
                        if (ImGui.MenuItem("保存"))
                        {
                            s.WindowShouldFreeze(true);
                            _saver.Value = model.Save(out _saver.Label, out _saver.Path);
                            s.WindowShouldFreeze(false);
                        }
                        if (ImGui.MenuItem("アニメーション", model is SkeletalModel))
                        {
                            if (_swapper.IsAware)
                            {
                                s.Renderer.Options.RemoveAnimations();
                                s.Renderer.Options.AnimateMesh(true);
                                s.WindowShouldClose(true, false);
                            }
                            else
                            {
                                _swapper.Title = "スケルタルアニメーション";
                                _swapper.Description = "モデルにアニメーションを適用します。\nアニメーションを抽出できるよう、このウィンドウは一度閉じます！\n\n";
                                _swapper.Content = () =>
                                {
                                    s.Renderer.Options.RemoveAnimations();
                                    s.Renderer.Options.AnimateMesh(true);
                                };
                                _swapper.Value = true;
                            }
                        }
                        if (ImGui.MenuItem("スケルトンツリー", model is SkeletalModel))
                        {
                            s.Renderer.IsSkeletonTreeOpen = true;
                            ImGui.SetWindowFocus("Skeleton Tree");
                        }
                        doubleClick = ImGui.MenuItem("ここへ移動");

                        if (ImGui.MenuItem("削除")) s.Renderer.Options.RemoveModel(guid);
                        if (ImGui.MenuItem("選択解除")) s.Renderer.Options.SelectModel(Guid.Empty);
                        ImGui.Separator();
                        if (ImGui.MenuItem("パスをコピー")) ImGui.SetClipboardText(model.Path);
                    });
                    if (doubleClick)
                    {
                        s.Renderer.CameraOp.Teleport(model.GetTransform().Matrix.Translation, model.Box);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Image(s.Renderer.Options.Icons[model.Attachments.Icon].GetPointer(), new Vector2(_tableWidth));
                    TooltipCopy(model.Attachments.Tooltip);

                    ImGui.PopID();
                    i++;
                }

                ImGui.EndTable();
            }
        });
        ImGui.PopStyleVar();
    }

    private void DrawSockets(Snooper s)
    {
        MeshWindow("Sockets", s.Renderer, (icons, selectedModel) =>
        {
            var info = new SocketAttachementInfo { Guid = s.Renderer.Options.SelectedModel, Instance = selectedModel.SelectedInstance };
            foreach (var model in s.Renderer.Options.Models.Values)
            {
                if (!model.HasSockets || model.IsSelected) continue;
                if (ImGui.TreeNode($"{model.Name} [{model.Sockets.Count}]"))
                {
                    var i = 0;
                    foreach (var socket in model.Sockets)
                    {
                        var isAttached = socket.AttachedModels.Contains(info);
                        ImGui.PushID(i);
                        ImGui.BeginDisabled(selectedModel.Attachments.IsAttached && !isAttached);
                        switch (isAttached)
                        {
                            case false when ImGui.Button($"Attach to '{socket.Name}'"):
                                selectedModel.Attachments.Attach(model, selectedModel.GetTransform(), socket, info);
                                break;
                            case true when ImGui.Button($"Detach from '{socket.Name}'"):
                                selectedModel.Attachments.Detach(model, selectedModel.GetTransform(), socket, info);
                                break;
                        }
                        ImGui.EndDisabled();
                        ImGui.PopID();
                        i++;
                    }
                    ImGui.TreePop();
                }
            }
        });
    }

    private void DrawDetails(Snooper s)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        MeshWindow("Details", s.Renderer, (icons, model) =>
        {
            if (ImGui.BeginTable("model_details", 2, ImGuiTableFlags.SizingStretchProp))
            {
                NoFramePaddingOnY(() =>
                {
                    Layout("エンティティ");ImGui.Text($"  :  ({model.Type}) {model.Name}");
                    Layout("GUID");ImGui.Text($"  :  {s.Renderer.Options.SelectedModel.ToString(EGuidFormats.UniqueObjectGuid)}");
                    if (model is SkeletalModel skeletalModel)
                    {
                        Layout("スケルトン");ImGui.Text($"  :  {skeletalModel.Skeleton.Name}");
                        Layout("ボーン");ImGui.Text($"  :  x{skeletalModel.Skeleton.BoneCount}");
                    }
                    else
                    {
                        Layout("両面");ImGui.Text($"  :  {model.IsTwoSided}");
                    }
                    Layout("ソケット");ImGui.Text($"  :  x{model.Sockets.Count}");

                    ImGui.EndTable();
                });
            }
            if (ImGui.BeginTabBar("tabbar_details", ImGuiTabBarFlags.None))
            {
                if (ImGui.BeginTabItem("セクション###Sections") && ImGui.BeginTable("table_sections", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersOuterV | ImGuiTableFlags.NoSavedSettings, ImGui.GetContentRegionAvail()))
                {
                    ImGui.TableSetupColumn("番号", ImGuiTableColumnFlags.NoHeaderWidth | ImGuiTableColumnFlags.WidthFixed, _tableWidth);
                    ImGui.TableSetupColumn("マテリアル");
                    ImGui.TableHeadersRow();

                    for (var i = 0; i < model.Sections.Length; i++)
                    {
                        var section = model.Sections[i];
                        var material = model.Materials[section.MaterialIndex];

                        ImGui.PushID(i);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if (!section.Show)
                        {
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(1, 0, 0, .5f)));
                        }
                        else if (s.Renderer.Color == VertexColor.Sections)
                        {
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(section.Color, 0.5f)));
                        }

                        ImGui.Text(section.MaterialIndex.ToString("D"));
                        ImGui.TableNextColumn();
                        if (ImGui.Selectable(material.Name, s.Renderer.Options.SelectedSection == i, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            s.Renderer.Options.SelectSection(i);
                        }
                        Popup(() =>
                        {
                            s.Renderer.Options.SelectSection(i);
                            if (ImGui.MenuItem("表示", null, section.Show)) section.Show = !section.Show;
                            if (ImGui.MenuItem("入れ替え"))
                            {
                                if (_swapper.IsAware)
                                {
                                    s.Renderer.Options.SwapMaterial(true);
                                    s.WindowShouldClose(true, false);
                                }
                                else
                                {
                                    _swapper.Title = "マテリアル入れ替え";
                                    _swapper.Description = "マテリアルを入れ替えます。\nマテリアルを抽出できるよう、このウィンドウは一度閉じます！\n\n";
                                    _swapper.Content = () => s.Renderer.Options.SwapMaterial(true);
                                    _swapper.Value = true;
                                }
                            }
                            ImGui.Separator();
                            if (ImGui.MenuItem("パスをコピー")) ImGui.SetClipboardText(material.Path);
                        });
                        ImGui.PopID();
                    }
                    ImGui.EndTable();

                    ImGui.EndTabItem();
                }

                _transformOpen = ImGui.BeginTabItem("トランスフォーム###Transform");
                if (_transformOpen)
                {
                    ImGui.PushID(0); ImGui.BeginDisabled(model.TransformsCount < 2);
                    ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                    ImGui.SliderInt("", ref model.SelectedInstance, 0, model.TransformsCount - 1, "インスタンス %i", ImGuiSliderFlags.AlwaysClamp);
                    ImGui.EndDisabled(); ImGui.PopID();

                    if (ImGui.BeginTable("guizmo_controls", 2, ImGuiTableFlags.SizingStretchProp))
                    {
                        var t = model.Transforms[model.SelectedInstance];
                        var c = _guizmoOperation switch
                        {
                            OPERATION.TRANSLATE => 0,
                            OPERATION.ROTATE => 1,
                            OPERATION.SCALE => 2,
                            _ => 3
                        };

                        Layout("操作          ");
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.6f);
                        ImGui.PushID(1);ImGui.Combo("", ref c, "移動\0回転\0拡大縮小\0統合\0");
                        ImGui.PopID();ImGui.SameLine();if (ImGui.Button("リセット")) t.Reset();
                        Layout("位置");ImGui.Text(t.Position.ToString());
                        Layout("回転");ImGui.Text(t.Rotation.ToString());
                        Layout("スケール");ImGui.Text(t.Scale.ToString());

                        _guizmoOperation = c switch
                        {
                            0 => OPERATION.TRANSLATE,
                            1 => OPERATION.ROTATE,
                            2 => OPERATION.SCALE,
                            _ => OPERATION.UNIVERSAL
                        };

                        ImGui.EndTable();
                    }

                    ImGui.SeparatorText("手動入力");
                    model.Transforms[model.SelectedInstance].ImGuiTransform(s.Renderer.CameraOp.Speed / 100f);

                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("モーフターゲット###Morph Targets"))
                {
                    if (model is SkeletalModel { HasMorphTargets: true } skeletalModel)
                    {
                        const float width = 10;
                        var region = ImGui.GetContentRegionAvail();
                        var box = new Vector2(region.X - width, region.Y / 1.5f);

                        if (ImGui.BeginListBox("", box))
                        {
                            for (int i = 0; i < skeletalModel.Morphs.Count; i++)
                            {
                                ImGui.PushID(i);
                                if (ImGui.Selectable(skeletalModel.Morphs[i].Name, s.Renderer.Options.SelectedMorph == i))
                                {
                                    s.Renderer.Options.SelectMorph(i, skeletalModel);
                                }
                                ImGui.PopID();
                            }
                            ImGui.EndListBox();

                            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2f, 0f));
                            ImGui.SameLine(); ImGui.PushID(99);
                            ImGui.VSliderFloat("", box with { X = width }, ref skeletalModel.MorphTime, 0.0f, 1.0f, "", ImGuiSliderFlags.AlwaysClamp);
                            ImGui.PopID(); ImGui.PopStyleVar();
                            ImGui.Spacing();
                            ImGui.Text($"時間: {skeletalModel.MorphTime:P}%");
                        }
                    }
                    else CenteredTextColored(_errorColor, "選択メッシュにモーフターゲットがありません");
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        });
        ImGui.PopStyleVar();
    }

    private void DrawMaterialInspector(Dictionary<string, Texture> icons, UModel model, Section section)
    {
        var material = model.Materials[section.MaterialIndex];

        ImGui.Spacing();
        ImGui.Image(icons["material"].GetPointer(), new Vector2(24));
        ImGui.SameLine(); ImGui.AlignTextToFramePadding(); ImGui.Text(material.Name);
        ImGui.Spacing();

        ImGui.SeparatorText("パラメータ");
        material.ImGuiParameters();

        ImGui.SeparatorText("テクスチャ");
        if (material.ImGuiTextures(icons, model))
        {
            _tiOpen = true;
            ImGui.SetWindowFocus("Texture Inspector");
        }

        ImGui.SeparatorText("プロパティ");
        NoFramePaddingOnY(() =>
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
            if (ImGui.TreeNode("基本"))
            {
                material.ImGuiBaseProperties("base");
                ImGui.TreePop();
            }

            ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
            if (ImGui.TreeNode("スカラー"))
            {
                material.ImGuiDictionaries("scalars", material.Parameters.Scalars, true);
                ImGui.TreePop();
            }
            ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
            if (ImGui.TreeNode("スイッチ"))
            {
                material.ImGuiDictionaries("switches", material.Parameters.Switches, true);
                ImGui.TreePop();
            }
            ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
            if (ImGui.TreeNode("カラー"))
            {
                material.ImGuiColors(material.Parameters.Colors);
                ImGui.TreePop();
            }
            if (ImGui.TreeNode("全テクスチャ"))
            {
                material.ImGuiDictionaries("textures", material.Parameters.Textures);
                ImGui.TreePop();
            }
        });
    }

    private void DrawTextureInspector(Snooper s)
    {
        if (!_tiOpen) return;
        if (ImGui.Begin("Texture Inspector", ref _tiOpen, ImGuiWindowFlags.NoScrollbar))
        {
            if (s.Renderer.Options.TryGetModel(out var model) && s.Renderer.Options.TryGetSection(model, out var section))
            {
                (model.Materials[section.MaterialIndex].GetSelectedTexture() ?? s.Renderer.Options.Icons["noimage"]).ImGuiTextureInspector();
            }
        }
        ImGui.End();
    }

    private void DrawSkeletonTree(Snooper s)
    {
        if (!s.Renderer.IsSkeletonTreeOpen) return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("Skeleton Tree", ref s.Renderer.IsSkeletonTreeOpen, ImGuiWindowFlags.NoScrollbar))
        {
            if (s.Renderer.Options.TryGetModel(out var model) && model is SkeletalModel skeletalModel)
            {
                skeletalModel.Skeleton.ImGuiBoneBreadcrumb();
                if (ImGui.BeginTable("skeleton_tree", 2, ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.RowBg, ImGui.GetContentRegionAvail(), ImGui.GetWindowWidth()))
                {
                    ImGui.TableSetupColumn("ボーン", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.NoHeaderWidth | ImGuiTableColumnFlags.WidthFixed, _tableWidth);
                    skeletalModel.Skeleton.ImGuiBoneHierarchy();
                    ImGui.EndTable();
                }
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void Draw3DViewport(Snooper s)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        Window("3D Viewport", () =>
        {
            var largest = ImGui.GetContentRegionAvail();
            largest.X -= ImGui.GetScrollX();
            largest.Y -= ImGui.GetScrollY();

            var size = new Vector2(largest.X, largest.Y);
            var pos = ImGui.GetWindowPos();
            var fHeight = ImGui.GetFrameHeight();

            s.Renderer.CameraOp.AspectRatio = size.X / size.Y;
            ImGui.Image(s.Framebuffer.GetPointer(), size, new Vector2(0, 1), new Vector2(1, 0));

            // JP機能: モデルを選択しているだけで、移動/回転/拡縮が一体化したギズモを常に表示する。
            // (操作種別は _guizmoOperation。既定は UNIVERSAL=移動+回転+拡縮 一体型。Transformタブの切替も反映)
            {
                ImGuizmo.SetDrawlist(ImGui.GetWindowDrawList());
                ImGuizmo.SetRect(pos.X, pos.Y + fHeight, size.X, size.Y);
                DrawGuizmo(s);
            }

            if (!ImGuizmo.IsUsing())
            {
                if (ImGui.IsItemHovered())
                {
                    // if left button down while mouse is hover viewport
                    if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && !_viewportFocus)
                    {
                        _viewportFocus = true;
                        s.CursorState = CursorState.Grabbed;
                    }
                    if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    {
                        var guid = s.Renderer.Picking.ReadPixel(ImGui.GetMousePos(), ImGui.GetCursorScreenPos(), size);
                        s.Renderer.Options.SelectModel(guid);
                        ImGui.SetWindowFocus("Outliner");
                        ImGui.SetWindowFocus("Details");
                    }
                    // JP操作性: マウスホイールでズーム/ドリー(ビューポート上にカーソルがある時のみ)
                    var wheel = ImGui.GetIO().MouseWheel;
                    if (wheel != 0f)
                        s.Renderer.CameraOp.Dolly(wheel);
                    // JP操作性: 中ボタンドラッグでパン開始
                    if (ImGui.IsMouseDown(ImGuiMouseButton.Middle) && !_viewportPanning)
                    {
                        _viewportPanning = true;
                        s.CursorState = CursorState.Grabbed;
                    }
                }

                if (_viewportFocus && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {
                    s.Renderer.CameraOp.Modify(ImGui.GetIO().MouseDelta);
                }

                // JP操作性: 中ボタンドラッグでパン(開始後はビューポート外へ出ても継続)
                if (_viewportPanning && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
                {
                    s.Renderer.CameraOp.Pan(ImGui.GetIO().MouseDelta);
                }

                // if left button up and mouse was in viewport
                if (_viewportFocus && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    _viewportFocus = false;
                    s.CursorState = CursorState.Normal;
                }
                // JP操作性: 中ボタンを離したらパン終了
                if (_viewportPanning && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
                {
                    _viewportPanning = false;
                    s.CursorState = CursorState.Normal;
                }
            }

            // ===== JP UI改善: ビューポート右上ツールバーを半透明角丸パネルにまとめる =====
            var dpi = ImGui.GetWindowDpiScale();
            var drawList = ImGui.GetWindowDrawList();
            var panelBg = ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.09f, 0.85f));
            const float margin = 8f;
            const float pad = 6f;
            const float gap = 4f;
            var iconSize = 16f * dpi;

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(5f)); // アイコンボタンはコンパクトに固定(全体のFramePaddingに影響されない)
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, _accentColor with { W = 0.55f });
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, _accentColor with { W = 0.80f });

            var btnFull = iconSize + 10f; // image + 2*FramePadding(5)
            const int iconCount = 3;
            var barW = iconCount * btnFull + (iconCount - 1) * gap + pad * 2;
            var barH = btnFull + pad * 2;
            var barPos = new Vector2(size.X - barW - margin, fHeight + margin);

            ImGui.SetCursorPos(barPos);
            var barScreen = ImGui.GetCursorScreenPos();
            drawList.AddRectFilled(barScreen, barScreen + new Vector2(barW, barH), panelBg, 6f);

            ImGui.SetCursorPos(barPos + new Vector2(pad, pad));
            ImGui.ImageButton("grid_btn", s.Renderer.Options.Icons[s.Renderer.ShowGrid ? "square" : "square_off"].GetPointer(), new Vector2(iconSize));
            TooltipCheckbox("グリッド", ref s.Renderer.ShowGrid);
            ImGui.SameLine(0f, gap);
            ImGui.ImageButton("skybox_btn", s.Renderer.Options.Icons[s.Renderer.ShowSkybox ? "cube" : "cube_off"].GetPointer(), new Vector2(iconSize));
            TooltipCheckbox("スカイボックス", ref s.Renderer.ShowSkybox);
            ImGui.SameLine(0f, gap);
            ImGui.ImageButton("lights_btn", s.Renderer.Options.Icons[s.Renderer.ShowLights ? "light" : "light_off"].GetPointer(), new Vector2(iconSize));
            TooltipCheckbox("ライト", ref s.Renderer.ShowLights);

            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar();

            // ===== JP UI改善: 左下ステータス(FPS + 選択モデル名)を半透明帯で読みやすく =====
            var framerate = ImGui.GetIO().Framerate;
            var status = $"FPS: {framerate:0} ({1000.0f / framerate:0.##} ms)";
            if (s.Renderer.Options.TryGetModel(out var statusModel))
                status += $"   |   {statusModel.Name}";
            var statusSize = ImGui.CalcTextSize(status);
            var statusPos = new Vector2(margin, size.Y - statusSize.Y - margin);
            ImGui.SetCursorPos(statusPos);
            var statusScreen = ImGui.GetCursorScreenPos();
            drawList.AddRectFilled(statusScreen - new Vector2(5f, 3f), statusScreen + statusSize + new Vector2(5f, 3f), panelBg, 4f);
            ImGui.SetCursorPos(statusPos);
            ImGui.Text(status);

            const string label = "プレビューは保存後/ゲーム内の最終結果と異なる場合があります。";
            var labelSize = ImGui.CalcTextSize(label);
            var labelPos = new Vector2(size.X - labelSize.X - margin, size.Y - labelSize.Y - margin);
            ImGui.SetCursorPos(labelPos);
            ImGui.TextColored(new Vector4(0.60f, 0.60f, 0.62f, 1.00f), label);

        }, false);
        ImGui.PopStyleVar();
    }

    private void DrawGuizmo(Snooper s)
    {
        var enableGuizmo = s.Renderer.Options.TryGetModel(out var selected) && selected.IsVisible;
        if (!enableGuizmo)
        {
            _gizmoWasUsing = false;
            return;
        }

        var view = s.Renderer.CameraOp.GetViewMatrix();
        var proj = s.Renderer.CameraOp.GetProjectionMatrix();
        var transform = selected.Transforms[selected.SelectedInstance];
        var preDrag = transform.LocalMatrix; // ドラッグ前(変更前)のローカル行列。undo用に保持。

        var matrix = transform.Matrix;
        if (ImGuizmo.Manipulate(ref view.M11, ref proj.M11, _guizmoOperation, MODE.LOCAL, ref matrix.M11) &&
            Matrix4x4.Invert(transform.Relation, out var invRelation))
        {
            // ^ long story short: there was issues with other transformation methods
            // that's one way of modifying root elements without breaking the world matrix
            transform.ModifyLocal(matrix * invRelation);
        }

        // JP機能(Ctrl+Z): ギズモのドラッグ開始時(前フレーム未使用→今フレーム使用)に、変更前の状態を取り消しスタックへ1回だけ積む。
        var usingNow = ImGuizmo.IsUsing();
        if (usingNow && !_gizmoWasUsing)
        {
            var model = selected;
            var instance = selected.SelectedInstance;
            var snapshot = preDrag;
            _undoStack.Push(() =>
            {
                try { model.Transforms[instance].ModifyLocal(snapshot); } catch { /* モデル削除済み等は無視 */ }
            });
        }
        _gizmoWasUsing = usingNow;
    }

    public static void Popup(Action content)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4f));
        if (ImGui.BeginPopupContextItem())
        {
            content();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    private void Modal(string title, bool condition, Action content)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4f));
        var pOpen = true;
        if (condition) ImGui.OpenPopup(title);
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(.5f));
        if (ImGui.BeginPopupModal(title, ref pOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            content();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
    }

    private void Window(string name, Action content, bool styled = true)
    {
        if (ImGui.Begin(name, ImGuiWindowFlags.NoScrollbar))
        {
            Controller.Normal();
            if (styled) PushStyleCompact();
            content();
            if (styled) PopStyleCompact();
            ImGui.PopFont();
        }
        ImGui.End();
    }

    private void MeshWindow(string name, Renderer renderer, Action<Dictionary<string, Texture>, UModel> content, bool styled = true)
    {
        Window(name, () =>
        {
            if (renderer.Options.TryGetModel(out var model)) content(renderer.Options.Icons, model);
            else NoMeshSelected();
        }, styled);
    }

    private void SectionWindow(string name, Renderer renderer, Action<Dictionary<string, Texture>, UModel, Section> content, bool styled = true)
    {
        MeshWindow(name, renderer, (icons, model) =>
        {
            if (renderer.Options.TryGetSection(model, out var section)) content(icons, model, section);
            else NoSectionSelected();
        }, styled);
    }

    private void AnimationWindow(string name, Renderer renderer, Action<Dictionary<string, Texture>, TimeTracker, List<Animation>> content, bool styled = true)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        Window(name, () => content(renderer.Options.Icons, renderer.Options.Tracker, renderer.Options.Animations), styled);
        ImGui.PopStyleVar();
    }

    private void PopStyleCompact() => ImGui.PopStyleVar(2);
    private void PushStyleCompact()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 3));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(0, 1));
    }

    public static void NoFramePaddingOnY(Action content)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 0));
        content();
        ImGui.PopStyleVar();
    }

    private void NoMeshSelected() => CenteredTextColored(_errorColor, "メッシュが選択されていません");
    private void NoSectionSelected() => CenteredTextColored(_errorColor, "セクションが選択されていません");
    private void CenteredTextColored(Vector4 color, string text)
    {
        var region = ImGui.GetContentRegionAvail();
        var size = ImGui.CalcTextSize(text);
        ImGui.SetCursorPos(new Vector2(
                ImGui.GetCursorPosX() + (region.X - size.X) / 2,
                ImGui.GetCursorPosY() + (region.Y - size.Y) / 2));
        Controller.Bold();
        ImGui.TextColored(color, text);
        ImGui.PopFont();
    }

    public static void Layout(string name, bool tooltip = false)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.AlignTextToFramePadding();
        ImGui.Spacing();ImGui.SameLine();ImGui.Text(name);
        if (tooltip) TooltipCopy(name);
        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
    }

    public static void TooltipCopy(string label, string text = null)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(label);
            ImGui.EndTooltip();
        }
        if (ImGui.IsItemClicked()) ImGui.SetClipboardText(text ?? label);
    }

    private static void TooltipCheckbox(string tooltip, ref bool value)
    {
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text($"{tooltip}: {value}");
            ImGui.EndTooltip();
        }
        if (ImGui.IsItemClicked()) value = !value;
    }

    private void Theme()
    {
        var style = ImGui.GetStyle();
        // JP UI改善: 余白/行間に少しゆとりを持たせ、詰まり感を解消(可読性・クリックしやすさ向上)
        style.WindowPadding = new Vector2(6f);
        style.FramePadding = new Vector2(6f, 4f);
        style.CellPadding = new Vector2(4f, 3f);
        style.ItemSpacing = new Vector2(7f, 5f);
        style.ItemInnerSpacing = new Vector2(5f, 4f);
        style.TouchExtraPadding = new Vector2(0f);
        style.IndentSpacing = 18f;
        style.ScrollbarSize = 12f;
        style.GrabMinSize = 10f;
        style.WindowBorderSize = 0f;
        style.ChildBorderSize = 0f;
        style.PopupBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.TabBorderSize = 0f;
        // JP UI改善(UEFN風): UE5エディタに寄せて角丸は控えめのフラット寄りに
        style.WindowRounding = 4f;
        style.ChildRounding = 3f;
        style.FrameRounding = 3f;
        style.PopupRounding = 4f;
        style.ScrollbarRounding = 6f;
        style.GrabRounding = 2f;
        style.LogSliderDeadzone = 0f;
        style.TabRounding = 3f;
        style.WindowTitleAlign = new Vector2(0.5f);
        style.WindowMenuButtonPosition = ImGuiDir.Right;
        style.ColorButtonPosition = ImGuiDir.Right;
        style.ButtonTextAlign = new Vector2(0.5f);
        style.SelectableTextAlign = new Vector2(0f);
        style.DisplaySafeAreaPadding = new Vector2(3f);

        style.Colors[(int) ImGuiCol.Text]                   = new Vector4(1.00f, 1.00f, 1.00f, 1.00f);
        style.Colors[(int) ImGuiCol.TextDisabled]           = new Vector4(0.50f, 0.50f, 0.50f, 1.00f);
        // JP UI改善(UEFN風): 青みを抜いたニュートラルなダークグレー基調(UE5エディタ準拠)
        style.Colors[(int) ImGuiCol.WindowBg]               = new Vector4(0.10f, 0.10f, 0.10f, 1.00f);
        style.Colors[(int) ImGuiCol.ChildBg]                = new Vector4(0.13f, 0.13f, 0.13f, 1.00f);
        style.Colors[(int) ImGuiCol.PopupBg]                = new Vector4(0.08f, 0.08f, 0.08f, 0.98f);
        style.Colors[(int) ImGuiCol.Border]                 = new Vector4(0.26f, 0.26f, 0.26f, 0.80f);
        style.Colors[(int) ImGuiCol.BorderShadow]           = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
        style.Colors[(int) ImGuiCol.FrameBg]                = new Vector4(0.16f, 0.16f, 0.16f, 1.00f); // 入力欄: ニュートラルグレー
        style.Colors[(int) ImGuiCol.FrameBgHovered]         = new Vector4(0.24f, 0.24f, 0.24f, 1.00f); // ホバーはグレー(UE風)
        style.Colors[(int) ImGuiCol.FrameBgActive]          = new Vector4(0.13f, 0.45f, 0.85f, 0.55f); // 編集中のみ青アクセント
        style.Colors[(int) ImGuiCol.TitleBg]                = new Vector4(0.09f, 0.09f, 0.09f, 1.00f);
        style.Colors[(int) ImGuiCol.TitleBgActive]          = new Vector4(0.09f, 0.09f, 0.09f, 1.00f);
        style.Colors[(int) ImGuiCol.TitleBgCollapsed]       = new Vector4(0.05f, 0.05f, 0.05f, 0.51f);
        style.Colors[(int) ImGuiCol.MenuBarBg]              = new Vector4(0.11f, 0.11f, 0.11f, 1.00f);
        style.Colors[(int) ImGuiCol.ScrollbarBg]            = new Vector4(0.02f, 0.02f, 0.02f, 0.53f);
        style.Colors[(int) ImGuiCol.ScrollbarGrab]          = new Vector4(0.31f, 0.31f, 0.31f, 1.00f);
        style.Colors[(int) ImGuiCol.ScrollbarGrabHovered]   = new Vector4(0.41f, 0.41f, 0.41f, 1.00f);
        style.Colors[(int) ImGuiCol.ScrollbarGrabActive]    = new Vector4(0.51f, 0.51f, 0.51f, 1.00f);
        style.Colors[(int) ImGuiCol.CheckMark]              = new Vector4(0.13f, 0.42f, 0.83f, 1.00f);
        style.Colors[(int) ImGuiCol.SliderGrab]             = new Vector4(0.13f, 0.42f, 0.83f, 0.78f);
        style.Colors[(int) ImGuiCol.SliderGrabActive]       = new Vector4(0.13f, 0.42f, 0.83f, 1.00f);
        // JP UI改善(UEFN風): ボタン=ニュートラルグレー、ホバーもグレー、押下のみ青
        style.Colors[(int) ImGuiCol.Button]                 = new Vector4(0.20f, 0.20f, 0.20f, 1.00f);
        style.Colors[(int) ImGuiCol.ButtonHovered]          = new Vector4(0.28f, 0.28f, 0.28f, 1.00f);
        style.Colors[(int) ImGuiCol.ButtonActive]           = new Vector4(0.13f, 0.45f, 0.85f, 0.85f);
        // JP UI改善(UEFN風): 選択行は青、ホバー(未選択)はグレー(UEのアウトライナ準拠)
        style.Colors[(int) ImGuiCol.Header]                 = new Vector4(0.13f, 0.45f, 0.85f, 0.70f);
        style.Colors[(int) ImGuiCol.HeaderHovered]          = new Vector4(0.26f, 0.26f, 0.26f, 1.00f);
        style.Colors[(int) ImGuiCol.HeaderActive]           = new Vector4(0.13f, 0.45f, 0.85f, 0.90f);
        style.Colors[(int) ImGuiCol.Separator]              = new Vector4(0.43f, 0.43f, 0.50f, 0.50f);
        style.Colors[(int) ImGuiCol.SeparatorHovered]       = new Vector4(0.10f, 0.40f, 0.75f, 0.78f);
        style.Colors[(int) ImGuiCol.SeparatorActive]        = new Vector4(0.10f, 0.40f, 0.75f, 1.00f);
        style.Colors[(int) ImGuiCol.ResizeGrip]             = new Vector4(0.13f, 0.42f, 0.83f, 0.39f);
        style.Colors[(int) ImGuiCol.ResizeGripHovered]      = new Vector4(0.12f, 0.41f, 0.81f, 0.78f);
        style.Colors[(int) ImGuiCol.ResizeGripActive]       = new Vector4(0.12f, 0.41f, 0.81f, 1.00f);
        // JP UI改善(UEFN風): タブはニュートラルグレー。選択タブは一段明るいグレー、ホバーもグレー
        style.Colors[(int) ImGuiCol.Tab]                    = new Vector4(0.11f, 0.11f, 0.11f, 1.00f);
        style.Colors[(int) ImGuiCol.TabHovered]             = new Vector4(0.26f, 0.26f, 0.26f, 1.00f);
        style.Colors[(int) ImGuiCol.TabSelected]            = new Vector4(0.18f, 0.18f, 0.18f, 1.00f);
        style.Colors[(int) ImGuiCol.TabDimmed]              = new Vector4(0.10f, 0.10f, 0.10f, 1.00f);
        style.Colors[(int) ImGuiCol.TabDimmedSelected]      = new Vector4(0.15f, 0.15f, 0.15f, 1.00f);
        style.Colors[(int) ImGuiCol.DockingPreview]         = new Vector4(0.26f, 0.59f, 0.98f, 0.70f);
        style.Colors[(int) ImGuiCol.DockingEmptyBg]         = new Vector4(0.20f, 0.20f, 0.20f, 1.00f);
        style.Colors[(int) ImGuiCol.PlotLines]              = new Vector4(0.61f, 0.61f, 0.61f, 1.00f);
        style.Colors[(int) ImGuiCol.PlotLinesHovered]       = new Vector4(1.00f, 0.43f, 0.35f, 1.00f);
        style.Colors[(int) ImGuiCol.PlotHistogram]          = new Vector4(0.90f, 0.70f, 0.00f, 1.00f);
        style.Colors[(int) ImGuiCol.PlotHistogramHovered]   = new Vector4(1.00f, 0.60f, 0.00f, 1.00f);
        style.Colors[(int) ImGuiCol.TableHeaderBg]          = new Vector4(0.09f, 0.09f, 0.09f, 1.00f);
        style.Colors[(int) ImGuiCol.TableBorderStrong]      = new Vector4(0.28f, 0.28f, 0.28f, 0.60f);
        style.Colors[(int) ImGuiCol.TableBorderLight]       = new Vector4(0.28f, 0.28f, 0.28f, 0.60f);
        style.Colors[(int) ImGuiCol.TableRowBg]             = new Vector4(0.00f, 0.00f, 0.00f, 0.00f);
        style.Colors[(int) ImGuiCol.TableRowBgAlt]          = new Vector4(1.00f, 1.00f, 1.00f, 0.06f);
        style.Colors[(int) ImGuiCol.TextSelectedBg]         = new Vector4(0.26f, 0.59f, 0.98f, 0.35f);
        style.Colors[(int) ImGuiCol.DragDropTarget]         = new Vector4(1.00f, 1.00f, 0.00f, 0.90f);
        style.Colors[(int) ImGuiCol.NavCursor]              = new Vector4(0.26f, 0.59f, 0.98f, 1.00f);
        style.Colors[(int) ImGuiCol.NavWindowingHighlight]  = new Vector4(1.00f, 1.00f, 1.00f, 0.70f);
        style.Colors[(int) ImGuiCol.NavWindowingDimBg]      = new Vector4(0.80f, 0.80f, 0.80f, 0.20f);
        style.Colors[(int) ImGuiCol.ModalWindowDimBg]       = new Vector4(0.80f, 0.80f, 0.80f, 0.35f);
    }
}
