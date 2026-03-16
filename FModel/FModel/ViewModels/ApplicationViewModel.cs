using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.Utils;
using FModel.Extensions;
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate bool IsFeatureAvailableDelegate(IntPtr feature);

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

    public IEnumerable<EAssetCategory> Categories { get; } = AssetCategoryExtensions.GetBaseCategories();

    private bool _isAssetsExplorerVisible;
    public bool IsAssetsExplorerVisible
    {
        get => _isAssetsExplorerVisible;
        set
        {
            if (SetProperty(ref _isAssetsExplorerVisible, value))
            {
                // SelectedLeftTabIndex = value ? 1 : 2;
            }
        }
    }

    private int _selectedLeftTabIndex;
    public int SelectedLeftTabIndex
    {
        get => _selectedLeftTabIndex;
        set
        {
            if (value is < 0 or > 2) return;
            SetProperty(ref _selectedLeftTabIndex, value);
        }
    }

    public RightClickMenuCommand RightClickMenuCommand => _rightClickMenuCommand ??= new RightClickMenuCommand(this);
    private RightClickMenuCommand _rightClickMenuCommand;
    public MenuCommand MenuCommand => _menuCommand ??= new MenuCommand(this);
    private MenuCommand _menuCommand;
    public CopyCommand CopyCommand => _copyCommand ??= new CopyCommand(this);
    private CopyCommand _copyCommand;

    public ICommand CopyAssetPathNameCommand => _copyAssetPathNameCommand ??= new SimpleRelayCommand(OnCopyAssetPathName);
    private ICommand _copyAssetPathNameCommand;

    private void OnCopyAssetPathName(object parameter)
    {
        if (parameter is not IList items || items.Count == 0) return;

        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is not GameFile file) continue;

            var path = file.PathWithoutExtension;
            var contentIndex = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIndex > -1)
            {
                var prefix = path.Substring(0, contentIndex);
                var suffix = path.Substring(contentIndex + "/Content/".Length);
                var root = prefix.SubstringAfterLast('/');

                if (root.Equals("Engine", StringComparison.OrdinalIgnoreCase))
                    path = $"/Engine/{suffix}";
                else if (root.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase) || root.Equals("ShooterGame", StringComparison.OrdinalIgnoreCase))
                    path = $"/Game/{suffix}";
                else
                    path = $"/{root}/{suffix}";
            }
            else if (!path.StartsWith("/"))
            {
                path = $"/{path}";
            }

            paths.Add($"{path}.{file.NameWithoutExtension}");
        }

        if (paths.Count > 0)
            Clipboard.SetText(string.Join(Environment.NewLine, paths));
    }

    public ICommand SaveSoundCommand => _saveSoundCommand ??= new SimpleRelayCommand(OnSaveSound);
    private ICommand _saveSoundCommand;

    private void OnSaveSound(object parameter)
    {
        if (parameter is not IList items || items.Count == 0) return;

        var gameFiles = items.Cast<GameFile>().ToList();
        var uassetFiles = gameFiles.Where(x => x.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase)).ToList();
        var otherFiles = gameFiles.Except(uassetFiles).ToList();

        if (uassetFiles.Count > 0)
            RightClickMenuCommand.Execute(new object[] { "Assets_Save_Audio", uassetFiles });

        if (otherFiles.Count > 0)
            SaveEncodedAudioFiles(otherFiles);
    }

    private async void SaveEncodedAudioFiles(List<GameFile> files)
    {
        if (files.Count == 1)
        {
            var file = files[0];
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Audio",
                FileName = Path.ChangeExtension(file.Name, ".wav"),
                Filter = "WAV Files (*.wav)|*.wav",
                InitialDirectory = UserSettings.Default.AudioDirectory
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                await SaveEncodedAudioFile(file, saveFileDialog.FileName);
            }
        }
        else
        {
            var dialog = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "Select a folder to save the audio files",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == true)
            {
                await ApplicationService.ThreadWorkerView.Begin(token =>
                {
                    foreach (var file in files)
                    {
                        if (token.IsCancellationRequested) break;
                        var path = Path.Combine(dialog.SelectedPath, Path.ChangeExtension(file.Name, ".wav"));
                        SaveEncodedAudioFile(file, path).Wait();
                    }
                });
            }
        }
    }

    private async Task SaveEncodedAudioFile(GameFile file, string outputPath)
    {
        try
        {
            var data = await Task.Run(() => file.Read());
            var tempPath = Path.Combine(Path.GetTempPath(), file.Name);
            var extension = file.Extension.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension) || (!extension.Equals("rada") && !extension.Equals("binka")))
            {
                 if (file.Name.EndsWith(".rada", StringComparison.OrdinalIgnoreCase)) extension = "rada";
                 else if (file.Name.EndsWith(".binka", StringComparison.OrdinalIgnoreCase)) extension = "binka";
            }

            if (AudioPlayerViewModel.TryDecode(extension, tempPath, data, out var wavPath))
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(wavPath, outputPath);
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(Path.GetFileName(outputPath), outputPath, true);
                });
            }
            else
            {
                FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to convert {file.Name}", Constants.RED, true));
            }
        }
        catch (Exception e)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Error saving {file.Name}: {e.Message}", Constants.RED, true));
        }
    }

    private class SimpleRelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        public SimpleRelayCommand(Action<object> execute) => _execute = execute;
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _execute(parameter);
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }

    public string InitialWindowTitle => $"FModelJP5.0 ({Constants.APP_SHORT_COMMIT_ID})";
    public string GameDisplayName => CUE4Parse.Provider.GameDisplayName ?? "Unknown";
    public string TitleExtra => $"({UserSettings.Default.CurrentDir.UeVersion}){(Build != EBuildKind.Release ? $" ({Build})" : "")}";

    public LoadingModesViewModel LoadingModes { get; }
    public CustomDirectoriesViewModel CustomDirectories { get; }
    public FModel.ViewModels.CUE4Parse.CUE4ParseViewModel CUE4Parse { get; }
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

    CUE4Parse = new FModel.ViewModels.CUE4Parse.CUE4ParseViewModel();
        if (CUE4Parse.Provider != null)
        {
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
        }

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
        if (vm.SelectedDirectory == null) return null;

        UserSettings.Default.DiffGameDirectory = vm.SelectedDirectory.GameDirectory;
        UserSettings.Default.PerDirectory[vm.SelectedDirectory.GameDirectory] = vm.SelectedDirectory;
        return vm.SelectedDirectory;
    }
    public DirectorySettings AvoidEmptyGameDirectory(bool bAlreadyLaunched, bool allowDiffSelection = false)
    {
        var gameDirectory = UserSettings.Default.GameDirectory;
        if (!bAlreadyLaunched && !string.IsNullOrEmpty(gameDirectory) && UserSettings.Default.PerDirectory.TryGetValue(gameDirectory, out var currentDir))
            return currentDir;

        var gameLauncherViewModel = new GameSelectorViewModel(gameDirectory, allowDiffSelection)
        {
            SelectedDiffDirectory = (DirectorySettings) UserSettings.Default.DiffDir?.Clone()
        };
        var result = new DirectorySelector(gameLauncherViewModel).ShowDialog();
        if (!result.HasValue || !result.Value) return null;

        UserSettings.Default.GameDirectory = gameLauncherViewModel.SelectedDirectory.GameDirectory;
        if (string.IsNullOrEmpty(UserSettings.Default.GameDirectory))
            return null;

        if (!bAlreadyLaunched || UserSettings.Default.CurrentDir.Equals(gameLauncherViewModel.SelectedDirectory) && (allowDiffSelection && UserSettings.Default.DiffDir != null && UserSettings.Default.DiffDir.Equals(gameLauncherViewModel.SelectedDiffDirectory)))
            return gameLauncherViewModel.SelectedDirectory;

        UserSettings.Default.CurrentDir = gameLauncherViewModel.SelectedDirectory;
        UserSettings.Save();
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

        var requiredMainGuids = CollectRequiredAesGuids(CUE4Parse.Provider);
        var requiredDiffGuids = CollectRequiredAesGuids(CUE4Parse.DiffProvider);

        CUE4Parse.ClearProvider();
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken =>
        {
            var aes = BuildAesKeyPairs(AesManager.AesKeys, requiredMainGuids, cancellationToken);

            IEnumerable<KeyValuePair<FGuid, FAesKey>> secondAes = [];
            if (AesManager.DiffAesKeys != null)
                secondAes = BuildAesKeyPairs(AesManager.DiffAesKeys, requiredDiffGuids, cancellationToken);

            CUE4Parse.LoadVfs(aes, secondAes);
            AesManager.SetAesKeys();
        });
        RaisePropertyChanged(nameof(GameDisplayName));
    }

    private static HashSet<FGuid> CollectRequiredAesGuids(AbstractVfsFileProvider? provider)
    {
        var requiredGuids = new HashSet<FGuid> { Constants.ZERO_GUID };
        if (provider == null)
            return requiredGuids;

        foreach (var keyGuid in provider.RequiredKeys)
            requiredGuids.Add(keyGuid);

        foreach (var vfs in provider.UnloadedVfs)
            requiredGuids.Add(vfs.EncryptionKeyGuid);

        foreach (var vfs in provider.MountedVfs)
            requiredGuids.Add(vfs.EncryptionKeyGuid);

        return requiredGuids;
    }

    private static List<KeyValuePair<FGuid, FAesKey>> BuildAesKeyPairs(IEnumerable<FileItem> sourceKeys, HashSet<FGuid> requiredGuids, System.Threading.CancellationToken cancellationToken)
    {
        var result = new List<KeyValuePair<FGuid, FAesKey>>();

        foreach (var source in sourceKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (requiredGuids.Count > 0 && !requiredGuids.Contains(source.Guid))
                continue;

            var key = source.Key.Trim();
            if (key.Length != 66)
                key = Constants.ZERO_64_CHAR;

            result.Add(new KeyValuePair<FGuid, FAesKey>(source.Guid, new FAesKey(key)));
        }

        return result;
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

    public static async Task InitACL()
    {
        var aclPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", "CUE4Parse-Natives.dll");
        var packagedAclCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "CUE4Parse-Natives.dll"),
            Path.Combine(Environment.CurrentDirectory, "CUE4Parse-Natives.dll")
        };
        const string preferredAclUrl = "https://github.com/Fortniteleakjp/oo2core_9_Linux/raw/refs/heads/main/CUE4Parse-Natives.dll";
        string[] requiredAclExports = ["nAllocate", "nCompressedTracks_IsValid", "nReadACLData", "nReadCurveACLData"];

        string? TryGetExistingPackagedDll()
        {
            foreach (var candidate in packagedAclCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                        return candidate;
                }
                catch
                {
                    // ignored
                }
            }

            return null;
        }

        IEnumerable<string> EnumerateSearchRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRoot(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    if (Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
                }
                catch
                {
                    // ignored
                }
            }

            AddRoot(Environment.CurrentDirectory);
            AddRoot(AppContext.BaseDirectory);

            foreach (var start in roots.ToArray())
            {
                var dir = new DirectoryInfo(start);
                for (var i = 0; i < 8 && dir is not null; i++)
                {
                    AddRoot(dir.FullName);
                    dir = dir.Parent;
                }
            }

            return roots;
        }

        bool IsValidDll(string path)
        {
            if (!File.Exists(path)) return false;
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = NativeLibrary.Load(path);
                foreach (var export in requiredAclExports)
                {
                    if (!NativeLibrary.TryGetExport(handle, export, out _))
                        return false;
                }

                // Some older native builds may not implement this probe.
                // When present, run it for diagnostics, but do not hard-fail if it reports false.
                if (NativeLibrary.TryGetExport(handle, "IsFeatureAvailable", out var isFeatureAvailableExport))
                {
                    var isFeatureAvailable = Marshal.GetDelegateForFunctionPointer<IsFeatureAvailableDelegate>(isFeatureAvailableExport);
                    var aclFeatureName = Marshal.StringToHGlobalAnsi("ACL");
                    try
                    {
                        _ = isFeatureAvailable(aclFeatureName);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(aclFeatureName);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
            }
        }

        bool TryLoadDll(string path, out string? error)
        {
            error = null;
            if (!File.Exists(path))
            {
                error = "file does not exist";
                return false;
            }

            try
            {
                NativeLibrary.Load(path);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        bool TryUseBundledDll()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "CUE4Parse-Natives.dll"),
                Path.Combine(Environment.CurrentDirectory, "CUE4Parse-Natives.dll")
            };

            foreach (var root in EnumerateSearchRoots())
            {
                candidates.Add(Path.Combine(root, "CUE4Parse", "CUE4Parse-Natives", "bin", "Release", "CUE4Parse-Natives.dll"));
                candidates.Add(Path.Combine(root, "CUE4Parse", "CUE4Parse-Natives", "builddir", "Release", "CUE4Parse-Natives.dll"));
                candidates.Add(Path.Combine(root, "CUE4Parse", "CUE4Parse-Natives", "bin", "Debug", "CUE4Parse-Natives.dll"));
                candidates.Add(Path.Combine(root, "CUE4Parse", "CUE4Parse-Natives", "builddir", "Debug", "CUE4Parse-Natives.dll"));
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!IsValidDll(candidate))
                    continue;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(aclPath)!);
                    File.Copy(candidate, aclPath, true);
                    if (IsValidDll(aclPath))
                    {
                        FLogger.Append(ELog.Information, () => FLogger.Text($"Using local CUE4Parse-Natives.dll: {candidate}", Constants.WHITE, true));
                        return true;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return false;
        }

        var existingPackagedAcl = TryGetExistingPackagedDll();
        if (existingPackagedAcl is not null)
        {
            aclPath = existingPackagedAcl;
            FLogger.Append(ELog.Information, () => FLogger.Text($"Using packaged CUE4Parse-Natives.dll: {existingPackagedAcl}", Constants.WHITE, true));
            if (TryLoadDll(aclPath, out var packagedLoadError))
            {
                return;
            }

            FLogger.Append(ELog.Warning, () => FLogger.Text($"Packaged CUE4Parse-Natives.dll failed to load directly: {packagedLoadError}", Constants.YELLOW, true));
        }

        // If a local .data DLL is loadable, prefer it over download paths.
        if (TryLoadDll(aclPath, out _))
        {
            FLogger.Append(ELog.Information, () => FLogger.Text($"Using local CUE4Parse-Natives.dll: {aclPath}", Constants.WHITE, true));
            return;
        }

        async Task<bool> TryDownloadAndValidate(string url)
        {
            var dir = Path.GetDirectoryName(aclPath)!;
            var tempPath = Path.Combine(dir, $"CUE4Parse-Natives.{Guid.NewGuid():N}.tmp");

            try
            {
                Directory.CreateDirectory(dir);
                await ApplicationService.ApiEndpointView.DownloadFileAsync(url, tempPath);
                if (!File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
                    return false;

                if (!IsValidDll(tempPath))
                {
                    var size = new FileInfo(tempPath).Length;
                    FLogger.Append(ELog.Warning, () => FLogger.Text($"Downloaded CUE4Parse-Natives.dll is invalid and will be ignored. Url={url}, Size={size} bytes", Constants.YELLOW, true));
                    return false;
                }

                File.Copy(tempPath, aclPath, true);
                return IsValidDll(aclPath);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        var packagedDllWasPresent = existingPackagedAcl is not null;

        if (!IsValidDll(aclPath) && !TryUseBundledDll())
        {
            if (packagedDllWasPresent)
            {
                var len = File.Exists(aclPath) ? new FileInfo(aclPath).Length : -1;
                FLogger.Append(ELog.Error, () => FLogger.Text($"Packaged CUE4Parse-Natives.dll was present but could not be initialized. Path={aclPath}, Size={len} bytes", Constants.RED, true));
                return;
            }

            if (!await TryDownloadAndValidate(preferredAclUrl))
            {
                var len = File.Exists(aclPath) ? new FileInfo(aclPath).Length : -1;
                FLogger.Append(ELog.Error, () => FLogger.Text($"CUE4Parse-Natives.dll could not be initialized from local/bundled/local-build/download sources. Path={aclPath}, Size={len} bytes", Constants.RED, true));
                return;
            }
        }

        try
        {
            NativeLibrary.Load(aclPath);
        }
        catch (Exception e)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to load CUE4Parse-Natives: {e.Message}", Constants.RED, true));
        }
    }

    public static async ValueTask InitOodle()
    {
        if (File.Exists(OodleHelper.OODLE_DLL_NAME_OLD))
        {
            try
            {
                File.Delete(OodleHelper.OODLE_DLL_NAME_OLD);
            }
            catch { /* ignored */}
        }

        var oodlePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", OodleHelper.OODLE_DLL_NAME);

        if (!File.Exists(oodlePath))
        {
            await OodleHelper.DownloadOodleDllAsync(oodlePath);
        }

        OodleHelper.Initialize(oodlePath);
    }

    public static async ValueTask InitZlib()
    {
        var zlibPath = Path.Combine(UserSettings.Default.OutputDirectory, ".data", ZlibHelper.DLL_NAME);
        var zlibFileInfo = new FileInfo(zlibPath);
        if (!zlibFileInfo.Exists || zlibFileInfo.LastWriteTimeUtc < DateTime.UtcNow.AddMonths(-4))
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
