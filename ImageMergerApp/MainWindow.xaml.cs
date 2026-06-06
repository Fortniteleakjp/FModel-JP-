using Microsoft.Win32;
using SkiaSharp;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageMergerApp;

public partial class MainWindow : Window
{
    private readonly List<string> _tempFilePaths = new();
    private readonly string _defaultOutputDirectory;
    private byte[] _imageBuffer = Array.Empty<byte>();
    private Point _startPoint;
    private ListBoxItem? _draggedItem;

    public MainWindow()
    {
        InitializeComponent();
        SpacingSlider.Value = 0;
        _defaultOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ImageMerger");
        Directory.CreateDirectory(_defaultOutputDirectory);
        this.KeyDown += ImageMerger_KeyDown;
        this.Closing += MainWindow_Closing;
    }

    private string FILENAME => $"ImageMerger_{DateTime.Now:yyyyMMdd_HHmmss}.png";

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        foreach (var path in _tempFilePaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }

    private async void DrawPreview(object? sender, DragCompletedEventArgs e)
    {
        if (ImagePreview.Source != null)
            await DrawPreview().ConfigureAwait(false);
    }

    private async void SpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
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
        SetControlsEnabled(false);

        var margin = Convert.ToInt32(SpacingSlider.Value);
        int num = 1, curW = 0, curH = 0, maxWidth = 0, maxHeight = 0, lineMaxHeight = 0;
        int imagesPerRow = Convert.ToInt32(SizeSlider.Value);
        var positions = new Dictionary<int, SKPoint>();
        var images = new SKBitmap[ImagesListBox.Items.Count];

        for (var i = 0; i < images.Length; i++)
        {
            var item = (ListBoxItem)ImagesListBox.Items[i];
            if (item.Tag is false) continue;

            var path = item.ContentStringFormat;
            SKBitmap? image = null;
            try
            {
                image = SKBitmap.Decode(path);
            }
            catch
            {
                var bytes = await File.ReadAllBytesAsync(path);
                using var stream = new MemoryStream(bytes);
                image = SKBitmap.Decode(stream);
            }

            if (image == null) continue;
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
            if (maxWidth <= 0 || maxHeight <= 0) return;
            using var bmp = new SKBitmap(maxWidth - margin, maxHeight - margin, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bmp);
            canvas.Clear(SKColors.Transparent);

            for (var i = 0; i < images.Length; i++)
            {
                if (images[i] == null) continue;
                using (images[i])
                {
                    canvas.DrawBitmap(images[i], positions[i], new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true });
                }
            }

            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            _imageBuffer = data.ToArray();
            var stream = new MemoryStream(_imageBuffer);
            var photo = new BitmapImage();
            photo.BeginInit();
            photo.CacheOption = BitmapCacheOption.OnLoad;
            photo.StreamSource = stream;
            photo.EndInit();
            photo.Freeze();

            Dispatcher.Invoke(() => ImagePreview.Source = photo);
        }).ConfigureAwait(false);

        Dispatcher.Invoke(() => SetControlsEnabled(true));
    }

    private void SetControlsEnabled(bool enabled)
    {
        AddButton.IsEnabled = enabled;
        UpButton.IsEnabled = enabled;
        DownButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;
        SizeSlider.IsEnabled = enabled;
        SpacingSlider.IsEnabled = enabled;
        OpenImageButton.IsEnabled = enabled;
        SaveImageButton.IsEnabled = enabled;
        CopyImageButton.IsEnabled = enabled;
    }

    private async void OnImageAdd(object sender, RoutedEventArgs e)
    {
        var fileBrowser = new OpenFileDialog
        {
            Title = "画像を追加",
            InitialDirectory = _defaultOutputDirectory,
            Multiselect = true,
            Filter = "画像ファイル (*.png,*.bmp,*.jpg,*.jpeg,*.jfif,*.jpe,*.tiff,*.tif)|*.png;*.bmp;*.jpg;*.jpeg;*.jfif;*.jpe;*.tiff;*.tif|すべてのファイル (*.*)|*.*"
        };

        if (fileBrowser.ShowDialog() != true) return;
        await AddImagesFromFiles(fileBrowser.FileNames);
    }

    private async Task AddImagesFromFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".jfif" or ".tif" or ".tiff")
            {
                if (!File.Exists(file)) continue;
                if (ImagesListBox.Items.Cast<ListBoxItem>().Any(i => i.ContentStringFormat == file)) continue;

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
            SizeSlider.Value = Math.Min(ImagesListBox.Items.Count, Math.Round(Math.Sqrt(ImagesListBox.Items.Count)));
            await DrawPreview().ConfigureAwait(false);
        }
    }

    private async void ModifyItemInList(object sender, RoutedEventArgs e)
    {
        if (ImagesListBox.Items.Count == 0 || ImagesListBox.SelectedItems.Count == 0) return;
        if (sender is not Button button) return;

        var selected = ImagesListBox.SelectedItems.Cast<ListBoxItem>().ToList();
        if (!selected.Any()) return;

        switch (button.Name)
        {
            case "UpButton":
            {
                var ordered = selected.OrderBy(i => ImagesListBox.Items.IndexOf(i)).ToList();
                foreach (var item in ordered)
                {
                    var index = ImagesListBox.Items.IndexOf(item);
                    if (index > 0)
                    {
                        ImagesListBox.Items.RemoveAt(index);
                        ImagesListBox.Items.Insert(index - 1, item);
                    }
                }
                await DrawPreview().ConfigureAwait(false);
                break;
            }
            case "DownButton":
            {
                var ordered = selected.OrderByDescending(i => ImagesListBox.Items.IndexOf(i)).ToList();
                foreach (var item in ordered)
                {
                    var index = ImagesListBox.Items.IndexOf(item);
                    if (index >= 0 && index < ImagesListBox.Items.Count - 1)
                    {
                        ImagesListBox.Items.RemoveAt(index);
                        ImagesListBox.Items.Insert(index + 1, item);
                    }
                }
                await DrawPreview().ConfigureAwait(false);
                break;
            }
            case "DeleteButton":
            {
                foreach (var item in selected)
                    ImagesListBox.Items.Remove(item);
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

        var popout = new ImagePopout
        {
            Title = "結合後の画像",
            Width = ImagePreview.Source.Width,
            Height = ImagePreview.Source.Height,
            ImageCtrl = { Source = ImagePreview.Source }
        };
        popout.Show();
    }

    private void OnSaveImage(object sender, RoutedEventArgs e)
    {
        if (ImagePreview.Source == null) return;

        var saveFileDialog = new SaveFileDialog
        {
            Title = "画像を保存",
            FileName = FILENAME,
            InitialDirectory = _defaultOutputDirectory,
            Filter = "PNGファイル (*.png)|*.png|すべてのファイル (*.*)|*.*"
        };

        if (saveFileDialog.ShowDialog() != true) return;

        File.WriteAllBytes(saveFileDialog.FileName, _imageBuffer);
        MessageBox.Show(this, $"保存しました: {saveFileDialog.FileName}", "保存完了", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (_imageBuffer.Length == 0) return;
        CopyPngToClipboard(_imageBuffer);
    }

    private static void CopyPngToClipboard(byte[] pngBytes)
    {
        var image = BitmapFrame.Create(new MemoryStream(pngBytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Clipboard.SetImage(image);
    }

    private async void ImagesListBox_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ListBoxItem)))
        {
            var droppedData = e.Data.GetData(typeof(ListBoxItem)) as ListBoxItem;
            var target = GetVisualParent<ListBoxItem>((DependencyObject)e.OriginalSource);
            if (droppedData != null && target != null && droppedData != target)
            {
                var oldIdx = ImagesListBox.Items.IndexOf(droppedData);
                var newIdx = ImagesListBox.Items.IndexOf(target);
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
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await AddImagesFromFiles(files);
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
            return;
        }

        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image == null) return;

            var tempPath = Path.Combine(Path.GetTempPath(), $"clip_{Guid.NewGuid()}.png");
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(fs);
            }
            _tempFilePaths.Add(tempPath);
            await AddImagesFromFiles(new[] { tempPath });
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
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                DragDrop.DoDragDrop(ImagesListBox, _draggedItem, DragDropEffects.Move);
                _draggedItem = null;
            }
        }
    }

    private static T? GetVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child != null && child is not T)
        {
            child = VisualTreeHelper.GetParent(child)!;
        }
        return child as T;
    }

    private void OnVisibilityToggle(object sender, RoutedEventArgs e)
    {
        if (ImagesListBox.Items.Count > 0)
        {
            _ = DrawPreview();
        }
    }
}
