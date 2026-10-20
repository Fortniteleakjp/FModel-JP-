using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FModel.Settings;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.Views;

/// <summary>
/// WebView2 (Edge/Chromium) の &lt;video&gt; で再生するエンジン。webm など Media Foundation が開けない形式用。
/// 一時フォルダを仮想ホストに割り当ててページと動画を同じオリジンで配信し、操作は JS 呼び出し、状態はページからのメッセージで受け取る。
/// </summary>
public partial class VideoPlayerWindow
{
    private const string WebHost = "fmodel-video.example";

    private const string PlayerHtml = """
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        <style>
          html, body { margin: 0; height: 100%; background: #000; overflow: hidden; }
          video { width: 100%; height: 100%; object-fit: contain; display: block; outline: none; }
        </style>
        </head>
        <body>
        <video id="v" preload="auto" autoplay></video>
        <script>
          const v = document.getElementById('v');
          const post = m => window.chrome.webview.postMessage(m);
          const num = x => Number.isFinite(x) ? x : null;
          const query = new URLSearchParams(location.search);
          v.volume = Math.min(Math.max(parseFloat(query.get('volume')) || 0, 0), 1);
          v.addEventListener('loadedmetadata', () => post({ type: 'opened', duration: num(v.duration), width: v.videoWidth, height: v.videoHeight }));
          v.addEventListener('durationchange', () => post({ type: 'duration', duration: num(v.duration) }));
          v.addEventListener('timeupdate', () => post({ type: 'time', position: v.currentTime }));
          v.addEventListener('seeked', () => post({ type: 'time', position: v.currentTime }));
          v.addEventListener('play', () => post({ type: 'play' }));
          v.addEventListener('pause', () => post({ type: 'pause' }));
          v.addEventListener('ended', () => post({ type: 'ended' }));
          v.addEventListener('error', () => post({ type: 'error', code: v.error ? v.error.code : 0, message: v.error ? v.error.message : '' }));
          v.addEventListener('click', () => post({ type: 'click' }));
          document.addEventListener('keydown', e => {
            if (e.key === ' ' || e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
              e.preventDefault();
              post({ type: 'key', key: e.key });
            }
          });
          window.fm = {
            play: () => v.play().catch(e => post({ type: 'error', code: -1, message: String(e) })),
            pause: () => v.pause(),
            seek: t => { v.currentTime = t; },
            volume: x => { v.volume = x; }
          };
          v.src = query.get('src');
        </script>
        </body>
        </html>
        """;

    private static Task<CoreWebView2Environment> _webEnvironment;
    private Task _webViewInitialization;
    private string _webUrl;
    private TimeSpan _webDuration;
    private TimeSpan _webPosition;

    private void InitializeWebPlayer()
    {
        WebPlayer.DefaultBackgroundColor = System.Drawing.Color.Black;
    }

    /// <summary>
    /// WebView2 の環境はプロセスで 1 つ。ユーザーデータは出力フォルダの .data に置く (既定の exe 横は書き込めないことがある)。
    /// 自動再生は既定だとユーザー操作が要るので許可しておく
    /// </summary>
    private static Task<CoreWebView2Environment> GetWebEnvironmentAsync()
    {
        return _webEnvironment ??= Create();

        static async Task<CoreWebView2Environment> Create()
        {
            try
            {
                var output = UserSettings.Default.OutputDirectory;
                var userDataFolder = string.IsNullOrEmpty(output)
                    ? Path.Combine(Path.GetTempPath(), "FModel", "WebView2")
                    : Path.Combine(output, ".data", "WebView2");
                var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                return await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            }
            catch
            {
                _webEnvironment = null; // ランタイムを入れた後に再試行できるように
                throw;
            }
        }
    }

    private Task EnsureWebViewAsync()
    {
        if (WebPlayer.CoreWebView2 is not null)
            return Task.CompletedTask;
        return _webViewInitialization ??= Initialize();

        async Task Initialize()
        {
            try
            {
                await WebPlayer.EnsureCoreWebView2Async(await GetWebEnvironmentAsync());
            }
            catch
            {
                _webViewInitialization = null;
                throw;
            }

            var core = WebPlayer.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsWebMessageEnabled = true;
            Directory.CreateDirectory(TempRoot);
            core.SetVirtualHostNameToFolderMapping(WebHost, TempRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += OnWebMessageReceived;
        }
    }

    private async void StartWeb(int generation)
    {
        _backend = PlaybackBackend.Web;
        _webDuration = TimeSpan.Zero;
        _webPosition = TimeSpan.Zero;
        Player.Visibility = Visibility.Collapsed;

        try
        {
            await EnsureWebViewAsync();
        }
        catch (Exception e)
        {
            if (generation != _loadGeneration) return;
            Log.Error(e, "Could not initialize WebView2 to play {Name}", _displayName);
            var runtimeHint = e is WebView2RuntimeNotFoundException
                ? TryFindResource("UI_VideoPlayer_WebView2Missing") as string
                : null;
            ShowFailure(runtimeHint ?? e.Message);
            return;
        }

        if (generation != _loadGeneration || _tempFilePath is null) return;

        try
        {
            File.WriteAllText(Path.Combine(TempRoot, "player.html"), PlayerHtml);
        }
        catch (Exception e)
        {
            Log.Error(e, "Could not write the video player page");
            ShowFailure(e.Message);
            return;
        }

        // 仮想ホスト上の相対 URL。ファイル名に空白や日本語があってもよいようにセグメントごとにエスケープする
        var relative = string.Join('/', Path.GetRelativePath(TempRoot, _tempFilePath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(Uri.EscapeDataString));
        var volume = (VolumeSlider.Value / 100.0).ToString(CultureInfo.InvariantCulture);
        _webUrl = $"https://{WebHost}/player.html?src={Uri.EscapeDataString(relative)}&volume={volume}";

        WebPlayer.Visibility = Visibility.Visible;
        WebPlayer.CoreWebView2.Navigate(_webUrl);
    }

    private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 前の動画のページから遅れて届いたメッセージは無視する
        if (_backend != PlaybackBackend.Web || e.Source != _webUrl) return;

        JObject message;
        try
        {
            message = JObject.Parse(e.WebMessageAsJson);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Invalid message from the video player page");
            return;
        }

        switch ((string) message["type"])
        {
            case "opened":
                _webDuration = Seconds(message["duration"]);
                OnOpened((int?) message["width"] > 0);
                break;
            case "duration":
                _webDuration = Seconds(message["duration"]);
                if (SeekSlider.IsEnabled) UpdateDuration();
                UpdatePosition();
                break;
            case "time":
                _webPosition = Seconds(message["position"]);
                UpdatePosition();
                break;
            case "play":
                SetPlayingState(true);
                break;
            case "pause":
                SetPlayingState(false);
                UpdatePosition();
                break;
            case "ended":
                OnEnded();
                break;
            case "error":
            {
                var code = (int?) message["code"] ?? 0;
                var text = (string) message["message"];
                Log.Error("WebView2 could not play {Name}: {Code} {Message}", _displayName, code, text);
                // 4 = MEDIA_ERR_SRC_NOT_SUPPORTED (Chromium もコーデック非対応)
                ShowFailure(string.IsNullOrEmpty(text) ? $"MediaError {code}" : text);
                break;
            }
            case "click":
                TogglePlayPause();
                break;
            case "key":
                switch ((string) message["key"])
                {
                    case " ":
                        HandleKey(Key.Space);
                        break;
                    case "ArrowLeft":
                        HandleKey(Key.Left);
                        break;
                    case "ArrowRight":
                        HandleKey(Key.Right);
                        break;
                }
                break;
        }

        static TimeSpan Seconds(JToken token)
        {
            var value = (double?) token;
            return value is > 0 and < 1e9 ? TimeSpan.FromSeconds(value.Value) : TimeSpan.Zero;
        }
    }

    private void RunScript(string script)
    {
        if (_backend != PlaybackBackend.Web || WebPlayer.CoreWebView2 is null) return;
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await WebPlayer.CoreWebView2.ExecuteScriptAsync($"window.fm && {script}");
            }
            catch (Exception e)
            {
                Log.Warning(e, "Could not run a script in the video player page");
            }
        }
    }

    private void WebPlay() => RunScript("fm.play()");

    private void WebPause() => RunScript("fm.pause()");

    private void WebSeek(TimeSpan position)
    {
        _webPosition = position;
        RunScript($"fm.seek({position.TotalSeconds.ToString(CultureInfo.InvariantCulture)})");
    }

    private void WebSetVolume(double volume) => RunScript($"fm.volume({volume.ToString(CultureInfo.InvariantCulture)})");

    /// <summary>ページを空にして動画ファイルの掴みを外す (一時ファイルを消せるように)</summary>
    private void ReleaseWebPlayer()
    {
        _webUrl = null;
        _webDuration = TimeSpan.Zero;
        _webPosition = TimeSpan.Zero;
        WebPlayer.Visibility = Visibility.Collapsed;
        try
        {
            WebPlayer.CoreWebView2?.Navigate("about:blank");
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not reset the video player page");
        }
    }
}
