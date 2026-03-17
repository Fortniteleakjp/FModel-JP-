using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdonisUI.Controls;
using FModel.Extensions;
using FModel.Settings;
using FModel;
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
    private const string StatusWebSocketUrl = "wss://fljpapi.jp/api/v3/fortnitestatus";
    private const string TweetShareBaseUrl = "https://twitter.com/share";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly DispatcherTimer _refreshTimer;
    private List<DynamicBackgroundEntry> _entries = new();
    private string _latestResponseJson = "{}";
    private bool _isRefreshing;
    private int _previewLoadVersion;
    private readonly int _initialTab;
    private CancellationTokenSource? _statusSocketCts;
    private Task? _statusSocketTask;

    private static readonly Brush NeutralCardBackground = CreateBrush("#3316202A");
    private static readonly Brush NeutralCardBorder = CreateBrush("#3A7BA8");
    private static readonly Brush NeutralFooterBackground = CreateBrush("#40101724");
    private static readonly Brush NeutralFooterBorder = CreateBrush("#4D6EB5E8");
    private static readonly Brush NeutralPrimaryText = CreateBrush("#E7F4FF");
    private static readonly Brush NeutralSecondaryText = CreateBrush("#96C4E8");
    private static readonly Brush NeutralEditorBackground = CreateBrush("#1A2230");

    private static readonly Brush UpCardBackground = CreateBrush("#1F0E2E1B");
    private static readonly Brush UpCardBorder = CreateBrush("#4D4ADE80");
    private static readonly Brush UpFooterBackground = CreateBrush("#3320432D");
    private static readonly Brush UpFooterBorder = CreateBrush("#6657D68D");
    private static readonly Brush UpPrimaryText = CreateBrush("#E8FFF1");
    private static readonly Brush UpSecondaryText = CreateBrush("#8FF0B2");
    private static readonly Brush UpEditorBackground = CreateBrush("#16251B");

    private static readonly Brush DownCardBackground = CreateBrush("#2A2A1012");
    private static readonly Brush DownCardBorder = CreateBrush("#66E35D6A");
    private static readonly Brush DownFooterBackground = CreateBrush("#3D351417");
    private static readonly Brush DownFooterBorder = CreateBrush("#88E35D6A");
    private static readonly Brush DownPrimaryText = CreateBrush("#FFECEE");
    private static readonly Brush DownSecondaryText = CreateBrush("#FF9FAA");
    private static readonly Brush DownEditorBackground = CreateBrush("#24171A");
    private static readonly Brush NeutralBadgeBackground = CreateBrush("#264A5C73");
    private static readonly Brush NeutralBadgeBorder = CreateBrush("#5096C4E8");
    private static readonly Brush UpBadgeBackground = CreateBrush("#2A1C6A3D");
    private static readonly Brush UpBadgeBorder = CreateBrush("#667BE9A2");
    private static readonly Brush DownBadgeBackground = CreateBrush("#3A5A1E25");
    private static readonly Brush DownBadgeBorder = CreateBrush("#88F28C99");

    public DynamicBackgroundApiWindow(int initialTab = 0)
    {
        _initialTab = initialTab;
        InitializeComponent();
        ResponseJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");
        StatusJsonEditor.SyntaxHighlighting = AvalonExtensions.HighlighterSelector("json");

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(40)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApiTabControl.SelectedIndex = Math.Clamp(_initialTab, 0, ApiTabControl.Items.Count - 1);
        ApplyStatusTheme(null);
        await RefreshAsync();
        _refreshTimer.Start();
        StartStatusSocket();
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _statusSocketCts?.Cancel();
    }

    private void StartStatusSocket()
    {
        _statusSocketCts?.Cancel();
        _statusSocketCts = new CancellationTokenSource();
        _statusSocketTask = Task.Run(() => RunStatusSocketLoopAsync(_statusSocketCts.Token));
    }

    private async Task RunStatusSocketLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusApiStateTextBlock.Text = "WSS接続中...";
                    StatusTextBlock.Text = "ステータスWSSに接続中...";
                    ApplyStatusTheme(null);
                });

                await socket.ConnectAsync(new Uri(StatusWebSocketUrl), cancellationToken).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    StatusApiStateTextBlock.Text = "接続済み (受信待機中)";
                    StatusTextBlock.Text = "ステータスWSS接続済み";
                });

                await ReceiveStatusMessagesAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    StatusApiStateTextBlock.Text = $"切断: {ex.Message}";
                    StatusTextBlock.Text = "ステータスWSSが切断されました。再接続します...";
                    ApplyStatusTheme("DOWN");
                });
            }

            if (!cancellationToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveStatusMessagesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "close", cancellationToken).ConfigureAwait(false);
                    return;
                }

                stream.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var json = Encoding.UTF8.GetString(stream.ToArray());
            await ApplyStatusPayloadAsync(json).ConfigureAwait(false);
        }
    }

    private async Task ApplyStatusPayloadAsync(string json)
    {
        try
        {
            var root = JObject.Parse(json);
            var fnstatus = root["fnstatus"] as JObject;
            var queue = root["queue"] as JObject;

            var gameName = fnstatus?["name"]?.ToString()
                ?? root["game"]?.ToString()
                ?? root["name"]?.ToString()
                ?? "Fortnite";
            var serviceId = fnstatus?["serviceInstanceId"]?.ToString() ?? "-";
            var rawStatus = fnstatus?["status"]?.ToString() ?? "-";
            var message = fnstatus?["message"]?.ToString() ?? root["message"]?.ToString() ?? "-";
            var queueActiveToken = queue?["active"];
            var queueActive = queueActiveToken?.ToString() ?? "-";
            var expectedWaitToken = queue?["expectedWait"];
            var maintenanceCount = root["maintenance"] is JArray m ? m.Count.ToString() : "0";
            var displayStatus = FormatStatusLabel(rawStatus);
            var displayQueueState = FormatQueueState(queueActiveToken);
            var displayWait = FormatExpectedWait(expectedWaitToken, queueActiveToken);

            await Dispatcher.InvokeAsync(() =>
            {
                StatusJsonEditor.Document ??= new TextDocument();
                StatusJsonEditor.Document.Text = root.ToString(Formatting.Indented);

                GameNameTextBlock.Text = gameName;
                ServiceInstanceIdTextBlock.Text = serviceId;
                FortniteStatusTextBlock.Text = displayStatus;
                RawStatusTextBlock.Text = rawStatus;
                StatusBadgeTextBlock.Text = displayStatus;
                FortniteMessageTextBlock.Text = message;
                QueueActiveTextBlock.Text = displayQueueState;
                QueueWaitTextBlock.Text = displayWait;
                MaintenanceCountTextBlock.Text = maintenanceCount;
                StatusUpdatedAtTextBlock.Text = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                StatusApiStateTextBlock.Text = "受信中 (リアルタイム更新)";
                StatusTextBlock.Text = $"ステータス更新: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
                ApplyStatusTheme(rawStatus);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                StatusApiStateTextBlock.Text = $"JSON解析エラー: {ex.Message}";
                ApplyStatusTheme("DOWN");
            });
        }
    }

    private void ApplyStatusTheme(string? status)
    {
        var normalized = status?.Trim().ToUpperInvariant();

        Brush cardBackground;
        Brush cardBorder;
        Brush footerBackground;
        Brush footerBorder;
        Brush primaryText;
        Brush secondaryText;
        Brush editorBackground;
        Brush badgeBackground;
        Brush badgeBorder;

        switch (normalized)
        {
            case "UP":
                cardBackground = UpCardBackground;
                cardBorder = UpCardBorder;
                footerBackground = UpFooterBackground;
                footerBorder = UpFooterBorder;
                primaryText = UpPrimaryText;
                secondaryText = UpSecondaryText;
                editorBackground = UpEditorBackground;
                badgeBackground = UpBadgeBackground;
                badgeBorder = UpBadgeBorder;
                break;
            case "DOWN":
                cardBackground = DownCardBackground;
                cardBorder = DownCardBorder;
                footerBackground = DownFooterBackground;
                footerBorder = DownFooterBorder;
                primaryText = DownPrimaryText;
                secondaryText = DownSecondaryText;
                editorBackground = DownEditorBackground;
                badgeBackground = DownBadgeBackground;
                badgeBorder = DownBadgeBorder;
                break;
            default:
                cardBackground = NeutralCardBackground;
                cardBorder = NeutralCardBorder;
                footerBackground = NeutralFooterBackground;
                footerBorder = NeutralFooterBorder;
                primaryText = NeutralPrimaryText;
                secondaryText = NeutralSecondaryText;
                editorBackground = NeutralEditorBackground;
                badgeBackground = NeutralBadgeBackground;
                badgeBorder = NeutralBadgeBorder;
                break;
        }

        StatusInfoBorder.Background = cardBackground;
        StatusInfoBorder.BorderBrush = cardBorder;
        StatusJsonBorder.Background = cardBackground;
        StatusJsonBorder.BorderBrush = cardBorder;
        FooterStatusBorder.Background = footerBackground;
        FooterStatusBorder.BorderBrush = footerBorder;
        StatusBadgeBorder.Background = badgeBackground;
        StatusBadgeBorder.BorderBrush = badgeBorder;

        StatusHeaderTextBlock.Foreground = primaryText;
        StatusApiStateTextBlock.Foreground = secondaryText;
        GameNameTextBlock.Foreground = primaryText;
        ServiceInstanceIdTextBlock.Foreground = primaryText;
        FortniteStatusTextBlock.Foreground = primaryText;
        RawStatusTextBlock.Foreground = primaryText;
        StatusBadgeTextBlock.Foreground = primaryText;
        FortniteMessageTextBlock.Foreground = primaryText;
        QueueActiveTextBlock.Foreground = primaryText;
        QueueWaitTextBlock.Foreground = primaryText;
        MaintenanceCountTextBlock.Foreground = primaryText;
        StatusUpdatedAtTextBlock.Foreground = primaryText;
        StatusTextBlock.Foreground = secondaryText;
        StatusJsonEditor.Background = editorBackground;
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private static string FormatStatusLabel(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "UP" => "稼働中",
            "DOWN" => "停止中 / 障害中",
            null or "" or "-" => "状態不明",
            _ => status!
        };
    }

    private static string FormatQueueState(JToken? queueActiveToken)
    {
        if (queueActiveToken is null || queueActiveToken.Type == JTokenType.Null)
            return "不明";

        if (queueActiveToken.Type == JTokenType.Boolean)
            return queueActiveToken.Value<bool>() ? "あり" : "なし";

        var raw = queueActiveToken.ToString();
        if (bool.TryParse(raw, out var isActive))
            return isActive ? "あり" : "なし";

        return raw;
    }

    private static string FormatExpectedWait(JToken? expectedWaitToken, JToken? queueActiveToken)
    {
        var queueActive = IsQueueActive(queueActiveToken);
        if (!queueActive)
            return "待機列なし";

        if (expectedWaitToken is null || expectedWaitToken.Type == JTokenType.Null)
            return "未定";

        if (expectedWaitToken.Type == JTokenType.Integer || expectedWaitToken.Type == JTokenType.Float)
        {
            var rawNumber = expectedWaitToken.Value<double>();
            return rawNumber > 0 ? $"{rawNumber:0.#}" : "未定";
        }

        var raw = expectedWaitToken.ToString();
        return string.IsNullOrWhiteSpace(raw) ? "未定" : raw;
    }

    private static bool IsQueueActive(JToken? queueActiveToken)
    {
        if (queueActiveToken is null || queueActiveToken.Type == JTokenType.Null)
            return false;

        if (queueActiveToken.Type == JTokenType.Boolean)
            return queueActiveToken.Value<bool>();

        return bool.TryParse(queueActiveToken.ToString(), out var isActive) && isActive;
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

    private void OnTweetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var shareUrl = BuildTweetShareUrl(
                ResolveTweetTargetUrl(),
                UserSettings.Default.TweetShareText,
                UserSettings.Default.TweetShareVia,
                UserSettings.Default.TweetShareHashtags,
                UserSettings.Default.TweetHashtagPosition);

            Process.Start(new ProcessStartInfo
            {
                FileName = shareUrl,
                UseShellExecute = true
            });

            StatusTextBlock.Text = "ツイート画面を開きました。";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ツイート画面の起動に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string ResolveTweetTargetUrl()
    {
        if (UserSettings.Default.TweetUseSelectedBackgroundUrl &&
            BackgroundsListBox.SelectedItem is DynamicBackgroundEntry selected &&
            !string.IsNullOrWhiteSpace(selected.BackgroundImage))
        {
            return selected.BackgroundImage;
        }

        return string.IsNullOrWhiteSpace(UserSettings.Default.TweetShareUrl)
            ? string.Empty
            : UserSettings.Default.TweetShareUrl.Trim();
    }

    private static string BuildTweetShareUrl(string url, string text, string via, string hashtags, ETweetHashtagPosition hashtagPosition)
    {
        var queryParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(url))
            queryParts.Add($"url={Uri.EscapeDataString(url)}");

        var finalText = EmbedHashtagsInText(text, hashtags, hashtagPosition);
        if (!string.IsNullOrWhiteSpace(finalText))
            queryParts.Add($"text={Uri.EscapeDataString(finalText)}");

        if (!string.IsNullOrWhiteSpace(via))
            queryParts.Add($"via={Uri.EscapeDataString(via)}");

        if (hashtagPosition == ETweetHashtagPosition.Separate && !string.IsNullOrWhiteSpace(hashtags))
            queryParts.Add($"hashtags={Uri.EscapeDataString(hashtags)}");

        var query = string.Join("&", queryParts);
        return string.IsNullOrWhiteSpace(query)
            ? TweetShareBaseUrl
            : $"{TweetShareBaseUrl}?{query}";
    }

    private static string EmbedHashtagsInText(string text, string hashtags, ETweetHashtagPosition position)
    {
        if (position == ETweetHashtagPosition.Separate || string.IsNullOrWhiteSpace(hashtags))
            return text;

        var tags = hashtags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.Length == 0)
            return text;

        var hashtagStr = string.Join(" ", tags.Select(t => t.StartsWith('#') ? t : $"#{t}"));

        return position switch
        {
            ETweetHashtagPosition.BeforeText => string.IsNullOrWhiteSpace(text) ? hashtagStr : $"{hashtagStr} {text}",
            ETweetHashtagPosition.AfterText => string.IsNullOrWhiteSpace(text) ? hashtagStr : $"{text} {hashtagStr}",
            ETweetHashtagPosition.AfterTextNewLine => string.IsNullOrWhiteSpace(text) ? hashtagStr : $"{text}\n{hashtagStr}",
            _ => text
        };
    }

    private sealed class DynamicBackgroundEntry
    {
        public string BackgroundImage { get; init; } = string.Empty;
        public string Stage { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string Key { get; init; } = string.Empty;
    }
}
