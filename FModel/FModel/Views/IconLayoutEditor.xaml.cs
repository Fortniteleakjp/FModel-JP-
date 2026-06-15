using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FModel.Creator.Layout;
using FModel.ViewModels;
using Microsoft.Win32;

namespace FModel.Views;

public partial class IconLayoutEditor
{
    private sealed class Handle
    {
        public Border Visual;
        public IconElementBase Element;
    }

    private readonly IconLayoutEditorViewModel _vm;
    private readonly List<Handle> _handles = new();

    private double _scale = 1;

    // ドラッグ状態
    private bool _dragging;
    private bool _resizing;
    private Point _dragStart;
    private IconElementBase _dragElem;
    private double _startX, _startY, _startW, _startH;

    // 選択中の要素（マウスホイールでサイズ変更する対象）
    private Handle _selected;

    public IconLayoutEditor()
    {
        _vm = new IconLayoutEditorViewModel();
        DataContext = _vm;
        InitializeComponent();

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.PreviewRendered += LayoutPreview;
        Loaded += (_, _) =>
        {
            RebuildHandles();
            LayoutPreview();
        };
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IconLayoutEditorViewModel.CurrentTemplate))
        {
            RebuildHandles();
            LayoutPreview();
        }
    }

    private void OnPreviewAreaSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();

    #region handles

    private void RebuildHandles()
    {
        foreach (var h in _handles)
            PreviewCanvas.Children.Remove(h.Visual);
        _handles.Clear();
        _selected = null;

        var t = _vm.CurrentTemplate;
        if (t == null) return;

        AddHandle(t.Preview, Application.Current.TryFindResource("IconLayout_Handle_Image") as string ?? "画像", Color.FromRgb(0x5E, 0xA3, 0xEC), isImage: true);
        AddHandle(t.Name, Application.Current.TryFindResource("IconLayout_Handle_Name") as string ?? "名前", Color.FromRgb(0x7C, 0xD9, 0x92), isImage: false);
        AddHandle(t.Description, Application.Current.TryFindResource("IconLayout_Handle_Description") as string ?? "説明", Color.FromRgb(0xE0, 0xB1, 0x5E), isImage: false);
        ApplySelectionVisual();
    }

    private void SetSelected(IconElementBase elem)
    {
        _selected = _handles.Find(h => h.Element == elem);
        ApplySelectionVisual();
    }

    private void ApplySelectionVisual()
    {
        foreach (var h in _handles)
            h.Visual.BorderThickness = new Thickness(h == _selected ? 3 : 1.5);
    }

    private void AddHandle(IconElementBase element, string label, Color color, bool isImage)
    {
        if (element == null) return;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.SizeAll,
            Tag = element,
            SnapsToDevicePixels = true
        };

        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(color),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(3, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        });

        if (isImage)
        {
            var grip = new Border
            {
                Width = 14,
                Height = 14,
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE,
                Tag = element
            };
            grip.MouseLeftButtonDown += Grip_MouseDown;
            grip.MouseMove += Handle_MouseMove;
            grip.MouseLeftButtonUp += Handle_MouseUp;
            grid.Children.Add(grip);
        }

        border.Child = grid;
        border.MouseLeftButtonDown += Handle_MouseDown;
        border.MouseMove += Handle_MouseMove;
        border.MouseLeftButtonUp += Handle_MouseUp;

        PreviewCanvas.Children.Add(border);
        _handles.Add(new Handle { Visual = border, Element = element });
    }

    #endregion

    #region preview layout

    private (double x, double y, double w, double h) ElemRect(IconElementBase e) => e switch
    {
        IconImageElement im => (im.X, im.Y, im.Width, im.Height),
        IconTextElement tx => (tx.X, tx.Y, tx.Width, Math.Max(tx.FontSize, 8)),
        _ => (e.X, e.Y, 50, 20)
    };

    private void LayoutPreview()
    {
        var img = _vm.PreviewImage;
        if (img == null || PreviewArea.ActualWidth < 2 || PreviewArea.ActualHeight < 2) return;

        // 描画された画像の実サイズを基準にする（オリジナル=テンプレートサイズ、組み込み=そのスタイルの出力サイズ）
        double srcW = img.PixelWidth, srcH = img.PixelHeight;
        if (srcW < 1 || srcH < 1) return;

        var availW = PreviewArea.ActualWidth - 16;
        var availH = PreviewArea.ActualHeight - 16;
        _scale = Math.Min(availW / srcW, availH / srcH);
        if (_scale <= 0 || double.IsNaN(_scale) || double.IsInfinity(_scale)) _scale = 1;

        PreviewCanvas.Width = srcW * _scale;
        PreviewCanvas.Height = srcH * _scale;
        PreviewImg.Width = srcW * _scale;
        PreviewImg.Height = srcH * _scale;

        // 配置ハンドルは「オリジナル」タイプのときだけ（自作レイアウトの編集対象）
        var showHandles = _vm.IsOriginal;
        foreach (var h in _handles)
        {
            if (!showHandles)
            {
                h.Visual.Visibility = Visibility.Collapsed;
                continue;
            }

            var r = ElemRect(h.Element);
            Canvas.SetLeft(h.Visual, r.x * _scale);
            Canvas.SetTop(h.Visual, r.y * _scale);
            h.Visual.Width = Math.Max(8, r.w * _scale);
            h.Visual.Height = Math.Max(8, r.h * _scale);
            h.Visual.Visibility = h.Element.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    #endregion

    #region drag

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not IconElementBase elem) return;
        SetSelected(elem);
        _dragElem = elem;
        _resizing = false;
        _dragStart = e.GetPosition(PreviewCanvas);
        var r = ElemRect(elem);
        _startX = r.x; _startY = r.y; _startW = r.w; _startH = r.h;
        _dragging = true;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not IconImageElement elem) return;
        SetSelected(elem);
        _dragElem = elem;
        _resizing = true;
        _dragStart = e.GetPosition(PreviewCanvas);
        var r = ElemRect(elem);
        _startW = r.w; _startH = r.h;
        _dragging = true;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _dragElem == null || _scale <= 0) return;

        var p = e.GetPosition(PreviewCanvas);
        var dx = (p.X - _dragStart.X) / _scale;
        var dy = (p.Y - _dragStart.Y) / _scale;

        var t = _vm.CurrentTemplate;
        if (_resizing && _dragElem is IconImageElement im)
        {
            im.Width = Math.Max(1, _startW + dx);
            im.Height = Math.Max(1, _startH + dy);
        }
        else if (t != null)
        {
            _dragElem.X = Math.Clamp(_startX + dx, 0, t.Width);
            _dragElem.Y = Math.Clamp(_startY + dy, 0, t.Height);
        }

        // 再描画はコアレスされるため、ハンドルだけは即座に追従させて操作感を保つ
        var h = _handles.Find(x => x.Element == _dragElem);
        if (h != null)
        {
            var r = ElemRect(_dragElem);
            Canvas.SetLeft(h.Visual, r.x * _scale);
            Canvas.SetTop(h.Visual, r.y * _scale);
            h.Visual.Width = Math.Max(8, r.w * _scale);
            h.Visual.Height = Math.Max(8, r.h * _scale);
        }

        e.Handled = true;
    }

    private void Handle_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _resizing = false;
        _dragElem = null;
        if (sender is FrameworkElement fe) fe.ReleaseMouseCapture();
        e.Handled = true;
    }

    #endregion

    #region buttons

    private void OnBrowseBackground(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Application.Current.TryFindResource("IconLayout_Dialog_BrowseBackground_Title") as string ?? "背景画像を選択",
            Filter = Application.Current.TryFindResource("IconLayout_Dialog_BrowseBackground_Filter") as string ?? "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.webp|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true || _vm.CurrentTemplate == null) return;

        IconLayoutRenderer.InvalidateBackgroundCache();
        _vm.CurrentTemplate.BackgroundImagePath = dlg.FileName;
        _vm.CurrentTemplate.BackgroundMode = EIconLayoutBackground.Image;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var result = AdonisUI.Controls.MessageBox.Show(
            Application.Current.TryFindResource("IconLayout_Msg_ResetConfirm") as string ?? "このカテゴリのレイアウトを既定値に戻します。よろしいですか？",
            Application.Current.TryFindResource("IconLayout_Caption_Confirm") as string ?? "確認",
            AdonisUI.Controls.MessageBoxButton.YesNo, AdonisUI.Controls.MessageBoxImage.Question);
        if (result == AdonisUI.Controls.MessageBoxResult.Yes)
            _vm.ResetCurrent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.Save();
        AdonisUI.Controls.MessageBox.Show(
            Application.Current.TryFindResource("IconLayout_Msg_Saved") as string ?? "レイアウトを保存しました。\n次回アイコンを生成（アセットを開く/再読み込み）したときに反映されます。",
            Application.Current.TryFindResource("IconLayout_Caption_Save") as string ?? "保存",
            AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Information);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>選択中の要素をマウスホイールで拡大縮小（テキスト=フォントサイズ、画像=拡縮）。</summary>
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_vm.IsOriginal || _selected?.Element == null) return;

        switch (_selected.Element)
        {
            case IconTextElement tx:
                tx.FontSize = Math.Clamp(tx.FontSize + (e.Delta > 0 ? 2 : -2), 6, 400);
                break;
            case IconImageElement im:
                var factor = e.Delta > 0 ? 1.05 : 1.0 / 1.05;
                im.Width = Math.Max(1, im.Width * factor);
                im.Height = Math.Max(1, im.Height * factor);
                break;
        }

        e.Handled = true;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = Application.Current.TryFindResource("IconLayout_Dialog_Export_Title") as string ?? "アイコンレイアウト設定の書き出し",
            Filter = Application.Current.TryFindResource("IconLayout_Dialog_Json_Filter") as string ?? "JSON ファイル|*.json|すべてのファイル|*.*",
            FileName = "FModelJP_IconLayout.json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, _vm.ExportJson());
            AdonisUI.Controls.MessageBox.Show(
                string.Format(Application.Current.TryFindResource("IconLayout_Msg_Exported") as string ?? "レイアウト設定を書き出しました。\n{0}", dlg.FileName),
                Application.Current.TryFindResource("IconLayout_Caption_Export") as string ?? "書き出し",
                AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AdonisUI.Controls.MessageBox.Show(
                string.Format(Application.Current.TryFindResource("IconLayout_Msg_ExportFailed") as string ?? "書き出しに失敗しました: {0}", ex.Message),
                Application.Current.TryFindResource("IconLayout_Caption_Error") as string ?? "エラー",
                AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Application.Current.TryFindResource("IconLayout_Dialog_Import_Title") as string ?? "アイコンレイアウト設定の読み込み",
            Filter = Application.Current.TryFindResource("IconLayout_Dialog_Json_Filter") as string ?? "JSON ファイル|*.json|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            if (_vm.ImportJson(File.ReadAllText(dlg.FileName)))
                AdonisUI.Controls.MessageBox.Show(
                    Application.Current.TryFindResource("IconLayout_Msg_Imported") as string ?? "レイアウト設定を読み込みました。\n「保存」を押すと確定します。",
                    Application.Current.TryFindResource("IconLayout_Caption_Import") as string ?? "読み込み",
                    AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Information);
            else
                AdonisUI.Controls.MessageBox.Show(
                    Application.Current.TryFindResource("IconLayout_Msg_ImportInvalid") as string ?? "読み込みに失敗しました（形式が不正です）。",
                    Application.Current.TryFindResource("IconLayout_Caption_Error") as string ?? "エラー",
                    AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            AdonisUI.Controls.MessageBox.Show(
                string.Format(Application.Current.TryFindResource("IconLayout_Msg_ImportFailed") as string ?? "読み込みに失敗しました: {0}", ex.Message),
                Application.Current.TryFindResource("IconLayout_Caption_Error") as string ?? "エラー",
                AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Error);
        }
    }

    #endregion
}
