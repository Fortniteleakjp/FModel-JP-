using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels.Commands;
using FModel.Views;
using FModel.Views.Resources.Controls;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.ViewModels;

public class ApplicationViewModel : ViewModel
{
    private EBuildKind _build;
    public EBuildKind Build
    {
        get => _build;
        private init
        {
            SetProperty(ref _build, value);
            RaisePropertyChanged(nameof(TitleExtra));
        }
    }

    private FStatus _status;
    public FStatus Status
    {
        get => _status;
        private init => SetProperty(ref _status, value);
    }

    public RightClickMenuCommand RightClickMenuCommand => _rightClickMenuCommand ??= new RightClickMenuCommand(this);
    private RightClickMenuCommand _rightClickMenuCommand;
    public MenuCommand MenuCommand => _menuCommand ??= new MenuCommand(this);
    private MenuCommand _menuCommand;
    public CopyCommand CopyCommand => _copyCommand ??= new CopyCommand(this);
    private CopyCommand _copyCommand;

    public string InitialWindowTitle => $"FModel ({Constants.APP_SHORT_COMMIT_ID})";
    public string GameDisplayName => CUE4Parse.Provider.GameDisplayName ?? "Unknown";
    public string TitleExtra => $"({UserSettings.Default.CurrentDir.UeVersion}){(Build != EBuildKind.Release ? $" ({Build})" : "")}";

    public LoadingModesViewModel LoadingModes { get; }
    public CustomDirectoriesViewModel CustomDirectories { get; }
    public CUE4Parse.CUE4ParseViewModel CUE4Parse { get; }
    public SettingsViewModel SettingsView { get; }
    public AesManagerViewModel AesManager { get; }
    public AudioPlayerViewModel AudioPlayer { get; }

    public ApplicationViewModel()
    {
        Status = new FStatus();
#if DEBUG
        Build = EBuildKind.Debug;
#elif RELEASE
        Build = EBuildKind.Release;
#else
        Build = EBuildKind.Unknown;
#endif
        LoadingModes = new LoadingModesViewModel();

        UserSettings.Default.CurrentDir = AvoidEmptyGameDirectory(false);
        if (UserSettings.Default.CurrentDir is null)
        {
            //If no game is selected, many things will break before a shutdown request is processed in the normal way.
            //A hard exit is preferable to an unhandled expection in this case
            Environment.Exit(0);
        }

        UserSettings.Default.DiffDir = ResolveDiffDirectory();

        CUE4Parse = new CUE4Parse.CUE4ParseViewModel();
        CUE4Parse.Provider.VfsRegistered += (sender, count) =>
        {
            if (sender is not IAesVfsReader reader) return;
            Status.UpdateStatusLabel($"{count} Archives ({reader.Name})", "Registered");
            CUE4Parse.GameDirectory.Add(reader);
        };
        CUE4Parse.Provider.VfsMounted += (sender, count) =>
        {
            if (sender is not IAesVfsReader reader) return;
            Status.UpdateStatusLabel($"{count:N0} Packages ({reader.Name})", "Mounted");
            CUE4Parse.GameDirectory.Verify(reader);
        };
        CUE4Parse.Provider.VfsUnmounted += (sender, _) =>
        {
            if (sender is not IAesVfsReader reader) return;
            CUE4Parse.GameDirectory.Disable(reader);
        };

        if (CUE4Parse.DiffProvider != null)
        {
            CUE4Parse.DiffProvider.VfsRegistered += (sender, count) =>
            {
                if (sender is not IAesVfsReader reader)
                    return;

                CUE4Parse.DiffGameDirectory.Add(reader);
            };
        }

        CustomDirectories = new CustomDirectoriesViewModel();
        SettingsView = new SettingsViewModel();
        AesManager = new AesManagerViewModel(CUE4Parse);
        AudioPlayer = new AudioPlayerViewModel();

        Status.SetStatus(EStatusKind.Ready);
    }
    public static DirectorySettings ResolveDiffDirectory()
    {
        var diffPath = UserSettings.Default.DiffGameDirectory;
        if (string.IsNullOrEmpty(diffPath))
            return null;

        if (UserSettings.Default.PerDirectory.TryGetValue(diffPath, out var existing))
            return existing;

        var vm = new GameSelectorViewModel(diffPath);

        UserSettings.Default.DiffGameDirectory = vm.SelectedDirectory.GameDirectory;
        UserSettings.Default.PerDirectory[vm.SelectedDirectory.GameDirectory] = vm.SelectedDirectory;
        return vm.SelectedDirectory;
    }
    public DirectorySettings AvoidEmptyGameDirectory(bool bAlreadyLaunched, bool allowDiffSelection = false)
    {
        var gameDirectory = UserSettings.Default.GameDirectory;
        if (!bAlreadyLaunched && UserSettings.Default.PerDirectory.TryGetValue(gameDirectory, out var currentDir))
            return currentDir;

        var gameLauncherViewModel = new GameSelectorViewModel(gameDirectory, allowDiffSelection)
        {
            SelectedDiffDirectory = (DirectorySettings) UserSettings.Default.DiffDir?.Clone()
        };
        var result = new DirectorySelector(gameLauncherViewModel).ShowDialog();
        if (!result.HasValue || !result.Value) return null;

        UserSettings.Default.GameDirectory = gameLauncherViewModel.SelectedDirectory.GameDirectory;
        if (!bAlreadyLaunched || UserSettings.Default.CurrentDir.Equals(gameLauncherViewModel.SelectedDirectory) && (allowDiffSelection && UserSettings.Default.DiffDir != null && UserSettings.Default.DiffDir.Equals(gameLauncherViewModel.SelectedDiffDirectory)))
            return gameLauncherViewModel.SelectedDirectory;

        // UserSettings.Save(); // ??? change key then change game, key saved correctly what?
        UserSettings.Default.CurrentDir = gameLauncherViewModel.SelectedDirectory;
        RestartWithWarning();
        return null;
    }

    public void RestartWithWarning()
    {
        MessageBox.Show("変更を適用するためには、 FModel を再起動する必要があります。", "再起動が必要です", MessageBoxButton.OK, MessageBoxImage.Warning);
        Restart();
    }

    public void Restart()
    {
        var path = Path.GetFullPath(Environment.GetCommandLineArgs()[0]);
        if (path.EndsWith(".dll"))
        {
            new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                }
            }.Start();
        }
        else if (path.EndsWith(".exe"))
        {
            new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                }
            }.Start();
        }

        Application.Current.Shutdown();
    }

    public async Task UpdateProvider(bool isLaunch)
    {
        if (!isLaunch && !AesManager.HasChange) return;

        CUE4Parse.ClearProvider();
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            // TODO: refactor after release, select updated keys only
            var aes = AesManager.AesKeys.Select(x =>
            {
                cancellationToken.ThrowIfCancellationRequested(); // cancel if needed

                var k = x.Key.Trim();
                if (k.Length != 66) k = Constants.ZERO_64_CHAR;
                return new KeyValuePair<FGuid, FAesKey>(x.Guid, new FAesKey(k));
            });

            IEnumerable<KeyValuePair<FGuid, FAesKey>> secondAes = [];
            if (AesManager.DiffAesKeys != null)
            {
                secondAes = AesManager.DiffAesKeys.Select(x =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var k = x.Key.Trim();
                    if (k.Length != 66)
                        k = Constants.ZERO_64_CHAR;
                    return new KeyValuePair<FGuid, FAesKey>(x.Guid, new FAesKey(k));
                });
            }

            CUE4Parse.LoadVfs(aes, secondAes);
            AesManager.SetAesKeys();
        });
        RaisePropertyChanged(nameof(GameDisplayName));
    }

    public static async Task InitVgmStream()
    {
        var vgmZipFilePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", "vgmstream-win.zip");
        if (File.Exists(vgmZipFilePath)) return;

        await ApplicationService.ApiEndpointView.DownloadFileAsync("https://github.com/vgmstream/vgmstream/releases/latest/download/vgmstream-win.zip", vgmZipFilePath);
        if (new FileInfo(vgmZipFilePath).Length > 0)
        {
            var zipDir = Path.GetDirectoryName(vgmZipFilePath)!;
            await using var zipFs = File.OpenRead(vgmZipFilePath);
            using var zip = new ZipArchive(zipFs, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                var entryPath = Path.Combine(zipDir, entry.FullName);
                await using var entryFs = File.Create(entryPath);
                await using var entryStream = entry.Open();
                await entryStream.CopyToAsync(entryFs);
            }
        }
        else
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Could not download VgmStream", Constants.WHITE, true));
        }
    }

    public static async Task InitImGuiSettings(bool forceDownload)
    {
        var imgui = "imgui.ini";
        var imguiPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", imgui);

        if (File.Exists(imgui)) File.Move(imgui, imguiPath, true);
        if (File.Exists(imguiPath) && !forceDownload) return;

        await ApplicationService.ApiEndpointView.DownloadFileAsync($"https://cdn.fmodel.app/d/configurations/{imgui}", imguiPath);
        if (new FileInfo(imguiPath).Length == 0)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text("Could not download ImGui settings", Constants.WHITE, true));
        }
    }

    public static async ValueTask InitOodle()
    {
        var oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_DLL_NAME);
        if (File.Exists(OodleHelper.OODLE_DLL_NAME))
        {
            File.Move(OodleHelper.OODLE_DLL_NAME, oodlePath, true);
        }
        else if (!File.Exists(oodlePath))
        {
            await OodleHelper.DownloadOodleDllAsync(oodlePath);
        }

        OodleHelper.Initialize(oodlePath);
    }

    public static async ValueTask InitZlib()
    {
        var zlibPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", ZlibHelper.DLL_NAME);
        if (!File.Exists(zlibPath))
        {
            await ZlibHelper.DownloadDllAsync(zlibPath);
        }

        ZlibHelper.Initialize(zlibPath);
    }

    public static async Task InitDetex()
    {
        var detexPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", DetexHelper.DLL_NAME);
        if (File.Exists(DetexHelper.DLL_NAME))
        {
            File.Move(DetexHelper.DLL_NAME, detexPath, true);
        }
        else if (!File.Exists(detexPath))
        {
            await DetexHelper.LoadDllAsync(detexPath);
        }

        DetexHelper.Initialize(detexPath);
    }
}
