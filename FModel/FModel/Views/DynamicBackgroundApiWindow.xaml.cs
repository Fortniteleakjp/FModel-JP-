using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdonisUI.Controls;
using FModel.Extensions;
using ICSharpCode.AvalonEdit.Document;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.Views;

public partial class DynamicBackgroundApiWindow : AdonisWindow
{
    private const string ApiUrl = "https://fortnitecontent-website-prod07.ol.epicgames.com/content/api/pages/fortnite-game/dynamicbackgrounds";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly DispatcherTimer _refreshTimer;
    private List<DynamicBackgroundEntry> _entries = new();
    private string _latestResponseJson = "{}";
    private bool _isRefreshing;
    private int _previewLoadVersion;

    public DynamicBackgroundApiWindow()
    {
        InitializeComponent();
        ResponseJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(40)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        _refreshTimer.Start();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
    }

    private async void OnRefreshNowClick(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)
            return;

        try
        {
            _isRefreshing = true;
            StatusTextBlock.Text = "API 取得中...";

            var json = await HttpClient.GetStringAsync(ApiUrl);
            var root = JObject.Parse(json);
            _latestResponseJson = root.ToString(Formatting.Indented);

            var allEntries = root["backgrounds"]?["backgrounds"]
                ?.Select(background => new DynamicBackgroundEntry
                {
                    BackgroundImage = background?["backgroundimage"]?.ToString() ?? string.Empty,
                    Stage = background?["stage"]?.ToString() ?? "(none)",
                    Type = background?["_type"]?.ToString() ?? "(none)",
                    Key = background?["key"]?.ToString() ?? "(none)"
                })
                .ToList() ?? new List<DynamicBackgroundEntry>();

            _entries = allEntries
                .Where(x => x.Key.Equals("lobby", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (_entries.Count == 0)
                _entries = allEntries;

            BackgroundsListBox.ItemsSource = _entries;
            ResponseJsonEditor.Document ??= new TextDocument();
            ResponseJsonEditor.Document.Text = _latestResponseJson;

            if (_entries.Count > 0)
            {
                BackgroundsListBox.SelectedIndex = 0;
                ApplySelectedEntry(_entries[0]);
            }
            else
            {
                ApplySelectedEntry(null);
            }

            StatusTextBlock.Text = $"最終更新: {DateTime.Now:yyyy/MM/dd HH:mm:ss}  件数: {_entries.Count}  次回更新: 40秒後";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"取得失敗: {ex.Message}";
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void OnBackgroundSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackgroundsListBox.SelectedItem is DynamicBackgroundEntry selected)
            ApplySelectedEntry(selected);
    }

    private void ApplySelectedEntry(DynamicBackgroundEntry? selected)
    {
        if (selected is null)
        {
            LobbyImage.Source = null;
            ResponsePreviewImage.Source = null;
            KeyValueTextBlock.Text = "key: (none)";
            StageValueTextBlock.Text = "(none)";
            TypeValueTextBlock.Text = "(none)";
            UrlTextBox.Text = string.Empty;
            ResponsePreviewHintText.Text = "レスポンスで選択中の背景画像を表示します。";
            return;
        }

        KeyValueTextBlock.Text = $"key: {selected.Key}";
        StageValueTextBlock.Text = selected.Stage;
        TypeValueTextBlock.Text = selected.Type;
        UrlTextBox.Text = selected.BackgroundImage;

        _ = LoadPreviewImageAsync(selected);
    }

    private async Task LoadPreviewImageAsync(DynamicBackgroundEntry selected)
    {
        var version = ++_previewLoadVersion;

        if (!Uri.TryCreate(selected.BackgroundImage, UriKind.Absolute, out _))
        {
            if (version != _previewLoadVersion) return;
            LobbyImage.Source = null;
            ResponsePreviewImage.Source = null;
            ResponsePreviewHintText.Text = "画像URLが無効です。";
            return;
        }

        try
        {
            ResponsePreviewHintText.Text = "画像を読み込み中...";
            var imageBytes = await HttpClient.GetByteArrayAsync(selected.BackgroundImage).ConfigureAwait(false);
            var imageSource = await Task.Run(() => CreateImageSource(imageBytes)).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _previewLoadVersion) return;

                LobbyImage.Source = imageSource;
                ResponsePreviewImage.Source = imageSource;
                ResponsePreviewHintText.Text = $"key={selected.Key} / stage={selected.Stage}";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != _previewLoadVersion) return;

                LobbyImage.Source = null;
                ResponsePreviewImage.Source = null;
                ResponsePreviewHintText.Text = $"画像読み込み失敗: {ex.Message}";
            });
        }
    }

    private static ImageSource CreateImageSource(byte[] imageBytes)
    {
        using var stream = new MemoryStream(imageBytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async void OnSaveImageClick(object sender, RoutedEventArgs e)
    {
        if (BackgroundsListBox.SelectedItem is not DynamicBackgroundEntry selected || string.IsNullOrWhiteSpace(selected.BackgroundImage))
        {
            MessageBox.Show("保存できる画像が選択されていません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var suggestedName = $"lobbybg_{selected.Stage}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        var saveFileDialog = new SaveFileDialog
        {
            Title = "ロビー背景を保存",
            FileName = suggestedName,
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|All files (*.*)|*.*"
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        try
        {
            var imageBytes = await HttpClient.GetByteArrayAsync(selected.BackgroundImage);
            await File.WriteAllBytesAsync(saveFileDialog.FileName, imageBytes);
            StatusTextBlock.Text = $"画像を保存しました: {saveFileDialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像の保存に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnCopyImageClick(object sender, RoutedEventArgs e)
    {
        if (BackgroundsListBox.SelectedItem is not DynamicBackgroundEntry selected || string.IsNullOrWhiteSpace(selected.BackgroundImage))
        {
            MessageBox.Show("コピーできる画像が選択されていません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var imageBytes = await HttpClient.GetByteArrayAsync(selected.BackgroundImage);
            using var stream = new MemoryStream(imageBytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            Clipboard.SetImage(image);
            StatusTextBlock.Text = "画像をクリップボードにコピーしました。";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"画像のコピーに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class DynamicBackgroundEntry
    {
        public string BackgroundImage { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Key { get; init; } = string.Empty;
    }
}
