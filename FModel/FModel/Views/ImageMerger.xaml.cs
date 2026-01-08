using AdonisUI.Controls;
using FModel.Extensions;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using System.ComponentModel;
using Microsoft.Win32;
using Serilog;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FModel.Views;

public partial class ImageMerger
{
    private const string FILENAME = "Preview.png";
    private byte[] _imageBuffer;
    private Point _startPoint;
    private ListBoxItem _draggedItem;
    private readonly List<string> _tempFilePaths = new();
    private bool _isPanning;
    private Point _panStartPoint;
    private double _hOff;
    private double _vOff;

    public ImageMerger()
    {
        InitializeComponent();
        this.KeyDown += ImageMerger_KeyDown;
        this.Closing += ImageMerger_Closing;
    }

    private void ImageMerger_Closing(object sender, CancelEventArgs e)
    {
        foreach (var path in _tempFilePaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Log.Warning(ex, "Failed to delete temporary image file: {FilePath}", path); }
        }
    }

    private async void DrawPreview(object sender, DragCompletedEventArgs dragCompletedEventArgs)
    {
        if (ImagePreview.Source != null)
            await DrawPreview().ConfigureAwait(false);
    }

    private async void Click_DrawPreview(object sender, MouseButtonEventArgs e)
    {
        if (ImagePreview.Source != null)
            await DrawPreview().ConfigureAwait(false);
    }

    private async Task DrawPreview()
    {
        AddButton.IsEnabled = false;
        UpButton.IsEnabled = false;
        DownButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        SizeSlider.IsEnabled = false;
        OpenImageButton.IsEnabled = false;
        SaveImageButton.IsEnabled = false;
        CopyImageButton.IsEnabled = false;

        var margin = UserSettings.Default.ImageMergerMargin;
        int num = 1, curW = 0, curH = 0, maxWidth = 0, maxHeight = 0, lineMaxHeight = 0, imagesPerRow = Convert.ToInt32(SizeSlider.Value);
        var positions = new Dictionary<int, SKPoint>();
        var images = new SKBitmap[ImagesListBox.Items.Count];
        for (var i = 0; i < images.Length; i++)
        {
            var item = (ListBoxItem) ImagesListBox.Items[i];
            if (item.Tag is false) continue;
            var ms = new MemoryStream();
            var stream = new FileStream(item.ContentStringFormat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (item.ContentStringFormat.EndsWith(".tif"))
            {
                await using var tmp = new MemoryStream();
                await stream.CopyToAsync(tmp);
                System.Drawing.Image.FromStream(tmp).Save(ms, ImageFormat.Png);
            }
            else
            {
                await stream.CopyToAsync(ms);
            }

            var image = SKBitmap.Decode(ms.ToArray());
            positions[i] = new SKPoint(curW, curH);
            images[i] = image;

            if (image.Height > lineMaxHeight)
                lineMaxHeight = image.Height;

            if (num % imagesPerRow == 0)
            {
                maxWidth = curW + image.Width + margin;
                curH += lineMaxHeight + margin;
                if (curH > maxHeight)
                    maxHeight = curH;

                curW = 0;
                lineMaxHeight = 0;
            }
            else
            {
                maxHeight = curH + lineMaxHeight + margin;
                curW += image.Width + margin;
                if (curW > maxWidth)
                    maxWidth = curW;
            }

            num++;
        }

        await Task.Run(() =>
        {
            using var bmp = new SKBitmap(maxWidth - margin, maxHeight - margin, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);

            for (var i = 0; i < images.Length; i++)
            {
                if (images[i] == null) continue;
                using (images[i])
                {
                    canvas.DrawBitmap(images[i], positions[i], new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true });
                }
            }

            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = new MemoryStream(_imageBuffer = data.ToArray());
            var photo = new BitmapImage();
            photo.BeginInit();
            photo.CacheOption = BitmapCacheOption.OnLoad;
            photo.StreamSource = stream;
            photo.EndInit();
            photo.Freeze();

            Application.Current.Dispatcher.Invoke(delegate { ImagePreview.Source = photo; });
        }).ContinueWith(t =>
        {
            AddButton.IsEnabled = true;
            UpButton.IsEnabled = true;
            DownButton.IsEnabled = true;
            DeleteButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
            SizeSlider.IsEnabled = true;
            OpenImageButton.IsEnabled = true;
            SaveImageButton.IsEnabled = true;
            CopyImageButton.IsEnabled = true;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async void OnImageAdd(object sender, RoutedEventArgs e)
    {
        var fileBrowser = new OpenFileDialog
        {
            Title = "画像を追加",
            InitialDirectory = Path.Combine(UserSettings.Default.OutputDirectory, "Exports"),
            Multiselect = true,
            Filter = "画像ファイル (*.png,*.bmp,*.jpg,*.jpeg,*.jfif,*.jpe,*.tiff,*.tif)|*.png;*.bmp;*.jpg;*.jpeg;*.jfif;*.jpe;*.tiff;*.tif|すべてのファイル (*.*)|*.*"
        };
        var result = fileBrowser.ShowDialog();
        if (!result.HasValue || !result.Value) return;

        foreach (var file in fileBrowser.FileNames)
        {
                ImagesListBox.Items.Add(new ListBoxItem
                {
                    ContentStringFormat = file,
                    Content = Path.GetFileNameWithoutExtension(file),
                    Tag = true
                });
        }

        SizeSlider.Value = Math.Min(ImagesListBox.Items.Count, Math.Round(Math.Sqrt(ImagesListBox.Items.Count)));
        await DrawPreview().ConfigureAwait(false);
    }

    private async void ModifyItemInList(object sender, RoutedEventArgs e)
    {
        if (ImagesListBox.Items.Count <= 0 || ImagesListBox.SelectedItems.Count <= 0) return;
        var indices = ImagesListBox.SelectedItems.Cast<ListBoxItem>().Select(i => ImagesListBox.Items.IndexOf(i)).ToArray();
        var reloadImage = false;

        switch (((Button) sender).Name)
        {
            case "UpButton":
                {
                    if (indices.Length > 0 && indices[0] > 0)
                    {
                        for (var i = 0; i < ImagesListBox.Items.Count; i++)
                        {
                        if (!indices.Contains(i)) continue;
                        var item = (ListBoxItem) ImagesListBox.Items[i];
                        ImagesListBox.Items.Remove(item);
                            ImagesListBox.Items.Insert(i - 1, item);
                            item.IsSelected = true;
                            reloadImage = true;
                        }
                    }

                    ImagesListBox.SelectedItems.Add(indices);
                    if (reloadImage)
                    {
                        await DrawPreview().ConfigureAwait(false);
                    }

                    break;
                }
            case "DownButton":
                {
                    if (indices.Length > 0 && indices[^1] < ImagesListBox.Items.Count - 1)
                    {
                        for (var i = ImagesListBox.Items.Count - 1; i > -1; --i)
                        {
                        if (!indices.Contains(i)) continue;
                        var item = (ListBoxItem) ImagesListBox.Items[i];
                        ImagesListBox.Items.Remove(item);
                            ImagesListBox.Items.Insert(i + 1, item);
                            item.IsSelected = true;
                            reloadImage = true;
                        }
                    }

                    if (reloadImage)
                    {
                        await DrawPreview().ConfigureAwait(false);
                    }

                    break;
                }
            case "DeleteButton":
                {
                    if (ImagesListBox.Items.Count > 0 && ImagesListBox.SelectedItems.Count > 0)
                    {
                        for (var i = ImagesListBox.SelectedItems.Count - 1; i >= 0; --i)
                            ImagesListBox.Items.Remove(ImagesListBox.SelectedItems[i]);
                    }

                    await DrawPreview().ConfigureAwait(false);

                    break;
                }
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        ImagesListBox.Items.Clear();
        ImagePreview.Source = null;
    }

    private void OnOpenImage(object sender, RoutedEventArgs e)
    {
        if (ImagePreview.Source == null) return;
        Helper.OpenWindow<AdonisWindow>("結合後の画像", () =>
        {
            new ImagePopout
            {
                Title = "結合後の画像",
                Width = ImagePreview.Source.Width,
                Height = ImagePreview.Source.Height,
                WindowState = ImagePreview.Source.Height > 1000 ? WindowState.Maximized : WindowState.Normal,
                ImageCtrl = { Source = ImagePreview.Source }
            }.Show();
        });
    }

    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(delegate
        {
            if (ImagePreview.Source == null) return;
            var saveFileDialog = new SaveFileDialog
            {
                Title = "画像を保存",
                FileName = FILENAME,
                InitialDirectory = UserSettings.Default.OutputDirectory,
                Filter = "PNGファイル (*.png)|*.png|すべてのファイル (*.*)|*.*"
            };
            var result = saveFileDialog.ShowDialog();
            if (!result.HasValue || !result.Value) return;

            using (var fs = new FileStream(saveFileDialog.FileName, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                fs.Write(_imageBuffer, 0, _imageBuffer.Length);
            }

            SaveCheck(saveFileDialog.FileName, Path.GetFileName(saveFileDialog.FileName));
        });
    }

    private static void SaveCheck(string path, string fileName)
    {
        if (File.Exists(path))
        {
            Log.Information("{FileName} successfully saved", fileName);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("正常に保存しました ", Constants.WHITE);
                FLogger.Link(fileName, path, true);
            });
        }
        else
        {
            Log.Error("{FileName} could not be saved", fileName);
            FLogger.Append(ELog.Error, () => FLogger.Text($"'{fileName}' を保存できませんでした", Constants.WHITE, true));
        }
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        ClipboardExtensions.SetImage(_imageBuffer, FILENAME);
    }

    private async void ImagesListBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ListBoxItem)))
        {
            var droppedData = e.Data.GetData(typeof(ListBoxItem)) as ListBoxItem;
            var target = GetVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);

            if (droppedData != null && target != null && droppedData != target)
            {
                int oldIdx = ImagesListBox.Items.IndexOf(droppedData);
                int newIdx = ImagesListBox.Items.IndexOf(target);

                if (oldIdx != -1 && newIdx != -1)
                {
                    ImagesListBox.Items.RemoveAt(oldIdx);
                    ImagesListBox.Items.Insert(newIdx, droppedData);
                    droppedData.IsSelected = true;
                    await DrawPreview().ConfigureAwait(false);
                }
            }
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file).ToLower();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".jfif" or ".tif" or ".tiff")
            {
                if (!File.Exists(file))
                {
                    Log.Warning("Dropped file not found: {FilePath}", file);
                    continue;
                }
                ImagesListBox.Items.Add(new ListBoxItem
                {
                    ContentStringFormat = file,
                    Content = Path.GetFileNameWithoutExtension(file),
                    Tag = true
                });
            }
        }

        if (ImagesListBox.Items.Count > 0)
        {
            SizeSlider.Value = Math.Min(ImagesListBox.Items.Count,
                Math.Round(Math.Sqrt(ImagesListBox.Items.Count)));

            await DrawPreview().ConfigureAwait(false);
        }
    }

    private async void ImageMerger_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
        {
            await PasteFromClipboardAsync();
        }
    }

    private async void OnPasteImage(object sender, RoutedEventArgs e)
    {
        await PasteFromClipboardAsync();
    }

    private async Task PasteFromClipboardAsync()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
            await AddImagesFromFiles(files);
        }
        else if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image != null)
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"clip_{Guid.NewGuid()}.png");
                try
                {
                    using (var fs = new FileStream(tempPath, FileMode.Create))
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        encoder.Save(fs);
                    }
                    _tempFilePaths.Add(tempPath);
                    await AddImagesFromFiles(new[] { tempPath });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to paste image from clipboard.");
                    FLogger.Append(ELog.Error, () => FLogger.Text("クリップボードからの画像の貼り付けに失敗しました。", Constants.RED));
                }
            }
        }
    }

    private async Task AddImagesFromFiles(string[] files)
    {
        foreach (string file in files)
        {
            string ext = Path.GetExtension(file).ToLower();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".jfif" or ".tif" or ".tiff")
            {
                if (!File.Exists(file))
                {
                    Log.Warning("File from clipboard not found: {FilePath}", file);
                    continue;
                }
                if (ImagesListBox.Items.Cast<ListBoxItem>().Any(i => i.ContentStringFormat == file))
                    continue;

                ImagesListBox.Items.Add(new ListBoxItem
                {
                    ContentStringFormat = file,
                    Content = Path.GetFileNameWithoutExtension(file),
                    Tag = true
                });
            }
        }

        if (ImagesListBox.Items.Count > 0)
        {
            SizeSlider.Value = Math.Min(ImagesListBox.Items.Count,
                Math.Round(Math.Sqrt(ImagesListBox.Items.Count)));

            await DrawPreview().ConfigureAwait(false);
        }
    }

    private void ImagesListBox_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ListBoxItem)))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            var scale = e.Delta > 0 ? 1.1 : 0.9;
            CanvasScaleTransform.ScaleX *= scale;
            CanvasScaleTransform.ScaleY *= scale;
        }
    }

    private void ImagesListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _startPoint = e.GetPosition(null);
        var dep = (DependencyObject)e.OriginalSource;

        while (dep != null && dep != ImagesListBox)
        {
            if (dep is ToggleButton) return;
            if (dep is ListBoxItem item)
            {
                _draggedItem = item;
                break;
            }
            dep = VisualTreeHelper.GetParent(dep);
        }
    }

    private void ImagesListBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _draggedItem != null)
        {
            Point mousePos = e.GetPosition(null);
            Vector diff = _startPoint - mousePos;
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                DragDrop.DoDragDrop(ImagesListBox, _draggedItem, DragDropEffects.Move);
                _draggedItem = null;
            }
        }
    }

    private async void OnVisibilityToggle(object sender, RoutedEventArgs e)
    {
        await DrawPreview().ConfigureAwait(false);
    }

    private static T GetVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child != null && !(child is T))
            child = VisualTreeHelper.GetParent(child);
        return child as T;
    }

    private void ImagePreview_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ImagePreview.Source == null) return;
        _panStartPoint = e.GetPosition(CanvasScrollViewer);
        _hOff = CanvasScrollViewer.HorizontalOffset;
        _vOff = CanvasScrollViewer.VerticalOffset;
        _isPanning = false;
        ImagePreview.CaptureMouse();
    }

    private void ImagePreview_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (ImagePreview.IsMouseCaptured)
        {
            var currentPoint = e.GetPosition(CanvasScrollViewer);
            var delta = currentPoint - _panStartPoint;

            if (!_isPanning && (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                _isPanning = true;
                ImagePreview.Cursor = Cursors.Hand;
            }

            if (_isPanning)
            {
                CanvasScrollViewer.ScrollToHorizontalOffset(_hOff - delta.X);
                CanvasScrollViewer.ScrollToVerticalOffset(_vOff - delta.Y);
            }
        }
    }

    private void ImagePreview_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ImagePreview.IsMouseCaptured)
        {
            ImagePreview.ReleaseMouseCapture();
            ImagePreview.Cursor = null;
            if (_isPanning)
            {
                e.Handled = true;
            }
        }
    }
}
