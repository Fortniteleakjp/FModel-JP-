using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    public IconLayoutEditor()
    {
        _vm = new IconLayoutEditorViewModel();
        DataContext = _vm;
        InitializeComponent();

        _vm.PropertyChanged += OnVmPropertyChanged;
        Loaded += (_, _) =>
        {
            RebuildHandles();
            LayoutPreview();
        };
    }

    private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IconLayoutEditorViewModel.CurrentTemplate))
            RebuildHandles();

        LayoutPreview();
    }

    private void OnPreviewAreaSizeChanged(object sender, SizeChangedEventArgs e) => LayoutPreview();

    #region handles

    private void RebuildHandles()
    {
        foreach (var h in _handles)
            PreviewCanvas.Children.Remove(h.Visual);
        _handles.Clear();

        var t = _vm.CurrentTemplate;
        if (t == null) return;

        AddHandle(t.Preview, "画像", Color.FromRgb(0x5E, 0xA3, 0xEC), isImage: true);
        AddHandle(t.Name, "名前", Color.FromRgb(0x7C, 0xD9, 0x92), isImage: false);
        AddHandle(t.Description, "説明", Color.FromRgb(0xE0, 0xB1, 0x5E), isImage: false);
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
        var t = _vm.CurrentTemplate;
        if (t == null || PreviewArea.ActualWidth < 2 || PreviewArea.ActualHeight < 2) return;

        var availW = PreviewArea.ActualWidth - 16;
        var availH = PreviewArea.ActualHeight - 16;
        _scale = Math.Min(availW / t.Width, availH / t.Height);
        if (_scale <= 0 || double.IsNaN(_scale) || double.IsInfinity(_scale)) _scale = 1;

        PreviewCanvas.Width = t.Width * _scale;
        PreviewCanvas.Height = t.Height * _scale;
        PreviewImg.Width = t.Width * _scale;
        PreviewImg.Height = t.Height * _scale;

        foreach (var h in _handles)
        {
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
            Title = "背景画像を選択",
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.webp|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true || _vm.CurrentTemplate == null) return;

        IconLayoutRenderer.InvalidateBackgroundCache();
        _vm.CurrentTemplate.BackgroundImagePath = dlg.FileName;
        _vm.CurrentTemplate.BackgroundMode = EIconLayoutBackground.Image;
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        var result = AdonisUI.Controls.MessageBox.Show(
            "このカテゴリのレイアウトを既定値に戻します。よろしいですか？", "確認",
            AdonisUI.Controls.MessageBoxButton.YesNo, AdonisUI.Controls.MessageBoxImage.Question);
        if (result == AdonisUI.Controls.MessageBoxResult.Yes)
            _vm.ResetCurrent();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.Save();
        AdonisUI.Controls.MessageBox.Show(
            "レイアウトを保存しました。\n次回アイコンを生成（アセットを開く/再読み込み）したときに反映されます。",
            "保存", AdonisUI.Controls.MessageBoxButton.OK, AdonisUI.Controls.MessageBoxImage.Information);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    #endregion
}
