using System;
using DiscordRPC;
using FModel.Extensions;
using FModel.Settings;
using FModel.ViewModels.CUE4Parse;
using Serilog;

namespace FModel.Services
{
    public sealed class DiscordService
    {
        public static DiscordHandler DiscordHandler { get; } = new();
    }

    public class DiscordHandler
    {
        private const string _APP_ID = "1392126547296522373";

        private RichPresence _currentPresence;
        private readonly DiscordRpcClient _client = new(_APP_ID);
        private readonly Timestamps _timestamps = new() {Start = DateTime.UtcNow};

        private readonly Assets _staticAssets = new()
        {
            LargeImageKey = "official_logo", SmallImageKey = "verified", SmallImageText = $"v{Constants.APP_VERSION} ({Constants.APP_SHORT_COMMIT_ID})"
        };

        private readonly Button[] _buttons =
        {
<<<<<<< HEAD
            new() {Label = "Discordサーバー", Url = Constants.DISCORD_LINK},
            new() {Label = "寄付", Url = Constants.DONATE_LINK}
=======
            new() {Label = "Discordサーバー", Url = Constants.DISCORD_LINK_JP},
            new() {Label = "寄付", Url = Constants.DONATE_LINK_JP}
>>>>>>> df23ed61115eb82197a2e1be8c9391c624a74e16
        };

        public void Initialize(string gameName)
        {
            _currentPresence = new RichPresence
            {
                Assets = _staticAssets,
                Timestamps = _timestamps,
                Buttons = _buttons,
                Details = $"{gameName} - 非アクティブ"
            };

            _client.OnReady += (_, args) => Log.Information("@{Username} ({UserId}) is now ready", args.User.Username, args.User.ID);
            _client.SetPresence(_currentPresence);
            _client.Initialize();
        }

        public void UpdatePresence(CUE4ParseViewModel viewModel) =>
            UpdatePresence(
                $"{viewModel.Provider.GameDisplayName ?? viewModel.Provider.ProjectName} - {viewModel.Provider.MountedVfs.Count}/{viewModel.Provider.MountedVfs.Count + viewModel.Provider.UnloadedVfs.Count} 個のフォルダ",
<<<<<<< HEAD
                $"Mode: {UserSettings.Default.LoadingMode.GetDescription()} - {viewModel.SearchVm.ResultsCount:### ### ###} 個のファイル".Trim());
=======
                $"{UserSettings.Default.LoadingMode.GetDescription()} - {viewModel.SearchVm.ResultsCount:### ### ###} 個のファイル".Trim());
>>>>>>> df23ed61115eb82197a2e1be8c9391c624a74e16

        public void UpdatePresence(string details, string state)
        {
            if (!_client.IsInitialized) return;
            _currentPresence.Details = details;
            _currentPresence.State = state;
            _client.SetPresence(_currentPresence);
            _client.Invoke();
        }

        public void UpdateButDontSavePresence(string details = null, string state = null)
        {
            if (!_client.IsInitialized) return;
            _client.SetPresence(new RichPresence
            {
                Assets = _staticAssets,
                Timestamps = _timestamps,
                Buttons = _buttons,
                Details = details ?? _currentPresence.Details,
                State = state ?? _currentPresence.State
            });
            _client.Invoke();
        }

        public void UpdateToSavedPresence()
        {
            if (!_client.IsInitialized) return;
            _client.SetPresence(_currentPresence);
            _client.Invoke();
        }

        public void Shutdown()
        {
            if (_client.IsInitialized)
                _client.Deinitialize();
        }

        public void Dispose()
        {
            if (!_client.IsDisposed)
                _client.Dispose();
        }
    }
}