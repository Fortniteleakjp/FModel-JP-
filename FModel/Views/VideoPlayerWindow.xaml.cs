using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AdonisUI.Controls;
using FModel.Settings;
using Microsoft.Win32;
using Serilog;

namespace FModel.Views;

/// <summary>
/// アーカイブ内の動画 (mp4/mov/webm 等) を再生するウィンドウ。
/// 再生エンジンは 2 つあり、操作バーは共通:
/// ・MediaElement (Windows の Media Foundation) … mp4/mov/m4v。H.264/AAC は標準で再生可
/// ・WebView2 (Edge/Chromium の &lt;video&gt;) … webm (VP8/VP9/AV1 + Vorbis/Opus)。MediaElement で再生できなかった動画もこちらで再試行する
/// どちらもストリームを受け取れないので、データを一時フォルダへ書き出してから再生する。
/// </summary>
public partial class VideoPlayerWindow : AdonisWindow
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "FModel", "Video");

    private enum PlaybackBackend
    {
        None,
        Native, // MediaElement
        Web // WebView2
    }

    private readonly DispatcherTimer _positionTimer;
    private PlaybackBackend _backend;
    private int _loadGeneration;
    private string _tempFilePath;
    private string _displayName;
    private bool _isPlaying;
    private bool _updatingSeek;

    public VideoPlayerWindow()
    {
        InitializeComponent();
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
        VolumeSlider.Value = UserSettings.Default.AudioPlayerVolume;
        InitializeWebPlayer();
    }

    /// <summary>開いていれば前面に出して差し替え、無ければ開いてから読み込む (UI スレッドから呼ぶこと)</summary>
    public static void Open(byte[] data, string archivePath)
    {
        var window = Application.Current.Windows.OfType<VideoPlayerWindow>().FirstOrDefault();
        if (window is null)
        {
            window = new VideoPlayerWindow();
            window.Show();
        }
        else
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        window.Load(data, archivePath);
    }

    public void Load(byte[] data, string archivePath)
    {
        ReleaseMedia();
        DeleteTempFiles();

        var generation = ++_loadGeneration;
        _displayName = Path.GetFileName(archivePath);
        Title = $"{TryFindResource("UI_VideoPlayer") as string ?? "Video Player"} - {_displayName}";
        ShowMessage(null);

        try
        {
            // 元のファイル名のまま書き出す (外部プレイヤーで開いたときに名前が分かるように)
            var directory = Path.Combine(TempRoot, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            _tempFilePath = Path.Combine(directory, _displayName);
            File.WriteAllBytes(_tempFilePath, data);
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not write the video to a temporary file");
            _tempFilePath = null;
            ShowMessage(e.Message);
            return;
        }

        if (PrefersWebPlayer(Path.GetExtension(_displayName)))
            StartWeb(generation);
        else
            StartNative();
    }

    /// <summary>Media Foundation (MediaElement) では開けない形式</summary>
    private static bool PrefersWebPlayer(string extension) => extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);

    private void StartNative()
    {
        _backend = PlaybackBackend.Native;
        Player.Visibility = Visibility.Visible;
        Player.Volume = VolumeSlider.Value / 100.0;
        Player.Source = new Uri(_tempFilePath);
        Play();
    }

    private TimeSpan Duration => _backend switch
    {
        PlaybackBackend.Native when Player.NaturalDuration.HasTimeSpan => Player.NaturalDuration.TimeSpan,
        PlaybackBackend.Web => _webDuration,
        _ => TimeSpan.Zero
    };

    private TimeSpan Position => _backend switch
    {
        PlaybackBackend.Native => Player.Position,
        PlaybackBackend.Web => _webPosition,
        _ => TimeSpan.Zero
    };

    private void SetPosition(TimeSpan position)
    {
        switch (_backend)
        {
            case PlaybackBackend.Native:
                Player.Position = position;
                break;
            case PlaybackBackend.Web:
                WebSeek(position);
                break;
        }
    }

    private void Play()
    {
        switch (_backend)
        {
            case PlaybackBackend.Native:
                Player.Play();
                _positionTimer.Start();
                SetPlayingState(true);
                break;
            case PlaybackBackend.Web:
                WebPlay(); // 状態はページの play イベントで反映する
                break;
        }
    }

    private void Pause()
    {
        switch (_backend)
        {
            case PlaybackBackend.Native:
                Player.Pause();
                _positionTimer.Stop();
                SetPlayingState(false);
                break;
            case PlaybackBackend.Web:
                WebPause();
                break;
        }

        UpdatePosition();
    }

    private void SetPlayingState(bool playing)
    {
        _isPlaying = playing;
        PlayPauseIcon.Data = (Geometry) FindResource(playing ? "PauseIcon" : "PlayIcon");
    }

    private void TogglePlayPause()
    {
        if (!PlayPauseButton.IsEnabled) return;
        if (_isPlaying) Pause();
        else Play();
    }

    private void Seek(TimeSpan offset)
    {
        if (!SeekSlider.IsEnabled) return;
        var duration = Duration;
        var position = Position + offset;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;
        SetPosition(position);
        UpdatePosition();
    }

    private void Stop()
    {
        if (_backend == PlaybackBackend.Native) Player.Stop();
        Pause();
        SetPosition(TimeSpan.Zero);
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        var position = Position;
        TimeText.Text = $"{Format(position)} / {Format(Duration)}";

        // ドラッグ中はスライダーを動かさない (つまみが指から逃げるため)
        if (SeekSlider.IsMouseCaptureWithin) return;
        _updatingSeek = true;
        SeekSlider.Value = position.TotalSeconds;
        _updatingSeek = false;
    }

    private void UpdateDuration()
    {
        SeekSlider.Maximum = Math.Max(Duration.TotalSeconds, 0.001);
        SeekSlider.SmallChange = 1;
        SeekSlider.LargeChange = Math.Max(SeekSlider.Maximum / 10, 1);
    }

    private static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");

    private void ShowMessage(string message)
    {
        MessageText.Text = message ?? string.Empty;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetControlsEnabled(bool enabled)
    {
        PlayPauseButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        SeekSlider.IsEnabled = enabled;
    }

    /// <summary>どちらのエンジンでも、メタデータを読めた時点で呼ぶ</summary>
    private void OnOpened(bool hasVideo)
    {
        UpdateDuration();
        SetControlsEnabled(true);
        if (!hasVideo)
        {
            // WebView2 は WPF の要素より手前に描かれるので、メッセージを見せるには隠す必要がある (音声は鳴り続ける)
            WebPlayer.Visibility = Visibility.Collapsed;
            ShowMessage(TryFindResource("UI_VideoPlayer_NoVideo") as string);
        }

        UpdatePosition();
    }

    private void OnEnded()
    {
        Stop();
    }

    private void ShowFailure(string detail)
    {
        _positionTimer.Stop();
        SetControlsEnabled(false);
        SetPlayingState(false);
        WebPlayer.Visibility = Visibility.Collapsed;
        var hint = TryFindResource("UI_VideoPlayer_Failed") as string ?? "This video could not be played.";
        ShowMessage(string.IsNullOrEmpty(detail) ? hint : $"{hint}\n\n{detail}");
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e) => OnOpened(Player.HasVideo);

    private void OnMediaEnded(object sender, RoutedEventArgs e) => OnEnded();

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_backend != PlaybackBackend.Native) return;

        // Media Foundation にデコーダーが無い動画でも Chromium なら再生できることがあるので WebView2 で再試行する
        Log.Warning(e.ErrorException, "MediaElement could not play {Name}, retrying with WebView2", _displayName);
        _positionTimer.Stop();
        Player.Close();
        Player.Source = null;
        Player.Visibility = Visibility.Collapsed;
        StartWeb(_loadGeneration);
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnVideoClick(object sender, MouseButtonEventArgs e) => TogglePlayPause();

    private void OnStopClick(object sender, RoutedEventArgs e) => Stop();

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || !SeekSlider.IsEnabled) return;
        var position = TimeSpan.FromSeconds(e.NewValue);
        SetPosition(position);
        TimeText.Text = $"{Format(position)} / {Format(Duration)}";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var volume = e.NewValue / 100.0;
        Player.Volume = volume;
        WebSetVolume(volume);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_tempFilePath is null || !File.Exists(_tempFilePath)) return;

        var extension = Path.GetExtension(_displayName);
        var dialog = new SaveFileDialog
        {
            FileName = _displayName,
            DefaultExt = extension,
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()}|*{extension}|All files|*.*"
        };
        if (Directory.Exists(UserSettings.Default.RawDataDirectory))
            dialog.InitialDirectory = UserSettings.Default.RawDataDirectory;
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.Copy(_tempFilePath, dialog.FileName, true);
            Log.Information("Successfully saved {FilePath}", dialog.FileName);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not save the video");
            System.Windows.MessageBox.Show(this, exception.Message, Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnOpenExternalClick(object sender, RoutedEventArgs e)
    {
        if (_tempFilePath is null || !File.Exists(_tempFilePath)) return;

        Pause();
        try
        {
            Process.Start(new ProcessStartInfo(_tempFilePath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not open the video in an external player");
            System.Windows.MessageBox.Show(this, exception.Message, Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key)) e.Handled = true;
    }

    /// <summary>Space=再生/一時停止、←/→=5 秒移動。WebView2 にフォーカスがあるときはページから転送されてくる</summary>
    private bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Space:
                TogglePlayPause();
                return true;
            case Key.Left:
                Seek(TimeSpan.FromSeconds(-5));
                return true;
            case Key.Right:
                Seek(TimeSpan.FromSeconds(5));
                return true;
            default:
                return false;
        }
    }

    private void ReleaseMedia()
    {
        _positionTimer.Stop();
        Player.Stop();
        Player.Close();
        Player.Source = null;
        Player.Visibility = Visibility.Visible;
        ReleaseWebPlayer();
        _backend = PlaybackBackend.None;
        SetControlsEnabled(false);
        SetPlayingState(false);
        _updatingSeek = true;
        SeekSlider.Value = 0;
        SeekSlider.Maximum = 1;
        _updatingSeek = false;
        TimeText.Text = "00:00 / 00:00";
    }

    /// <summary>
    /// 以前に書き出した一時ファイルを消す。外部プレイヤーが開いたままのものは消せないので次回に回す
    /// </summary>
    private static void DeleteTempFiles()
    {
        if (!Directory.Exists(TempRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(TempRoot))
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch
            {
                // 使用中
            }
        }
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        UserSettings.Default.AudioPlayerVolume = (float) VolumeSlider.Value;
        _loadGeneration++;
        ReleaseMedia();
        WebPlayer.Dispose();
        DeleteTempFiles();
    }
}
