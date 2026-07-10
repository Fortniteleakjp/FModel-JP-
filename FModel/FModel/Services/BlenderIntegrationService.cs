using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using AdonisUI.Controls;
using Microsoft.Win32;
using Newtonsoft.Json;
using Serilog;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;

namespace FModel.Services;

public static class BlenderIntegrationService
{
    private const int BridgePort = 24280;
    private const string BridgeServiceName = "FModelBlenderBridge";
    private const string BridgeResourceSuffix = "FModelBlenderBridge.py";

    public static bool ImportAnimations(IEnumerable<string> animationPaths)
    {
        var files = animationPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => File.Exists(path) && Path.GetExtension(path).Equals(".ueanim", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
            return false;

        var requestJson = JsonConvert.SerializeObject(new
        {
            command = "import_animations",
            files
        });

        var dependencyStatus = BlenderDependencyInspector.Inspect();
        var statusResponse = TrySendToRunningBlender(JsonConvert.SerializeObject(new { command = "status" }));
        if (string.Equals(statusResponse?.Service, BridgeServiceName, StringComparison.Ordinal))
        {
            if (statusResponse.Ok && statusResponse.UeFormatAvailable is false)
            {
                ShowUeFormatGuidance();
                return false;
            }

            // Older bridge scripts do not implement the status command. In that case,
            // use the local installation check before falling back to their import path.
            if (!statusResponse.Ok && !dependencyStatus.HasUeFormatInstallation)
            {
                ShowUeFormatGuidance();
                return false;
            }

            var importResponse = TrySendToRunningBlender(requestJson);
            if (importResponse is { Ok: true })
            {
                Log.Information("Sent {Count} UEFormat animation(s) to the running Blender bridge", files.Length);
                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"Blenderへ{files.Length}件のアニメーションを送信しました。", Constants.WHITE, true));
                return true;
            }
        }

        if (IsBlenderRunning())
        {
            ShowStartupBridgeGuidance(dependencyStatus);
            return false;
        }

        if (!dependencyStatus.HasUeFormatInstallation)
        {
            ShowUeFormatGuidance();
            return false;
        }

        try
        {
            var blenderExecutable = ResolveBlenderExecutable();
            if (blenderExecutable is null)
            {
                Log.Warning("Blender executable was not selected; UEFormat transfer was cancelled");
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text("Blenderの実行ファイルが見つからないため、移植を中止しました。", Constants.WHITE, true));
                return false;
            }

            var bridgePath = ExtractBridgeScript();
            var requestPath = WriteRequestFile(requestJson);
            var startInfo = new ProcessStartInfo
            {
                FileName = blenderExecutable,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(blenderExecutable) ?? Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("--python");
            startInfo.ArgumentList.Add(bridgePath);
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(requestPath);

            Process.Start(startInfo);
            Log.Information("Started Blender for UEFormat animation import: {Executable}", blenderExecutable);
            FLogger.Append(ELog.Information, () =>
                FLogger.Text("Blenderを起動しました。互換性のあるリグを選択するとアニメーションが読み込まれます。", Constants.WHITE, true));
            return true;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not start Blender for UEFormat animation import");
            FLogger.Append(ELog.Error, () =>
                FLogger.Text($"Blenderへの移植に失敗しました: {exception.Message}", Constants.WHITE, true));
            return false;
        }
    }

    private static BridgeResponse TrySendToRunningBlender(string requestJson)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, BridgePort);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(400)))
                return null;

            using var stream = client.GetStream();
            stream.ReadTimeout = 1000;
            stream.WriteTimeout = 1000;

            var requestBytes = Encoding.UTF8.GetBytes(requestJson + "\n");
            stream.Write(requestBytes, 0, requestBytes.Length);
            stream.Flush();

            var responseBytes = new byte[512];
            var responseLength = stream.Read(responseBytes, 0, responseBytes.Length);
            if (responseLength <= 0)
                return null;

            var response = Encoding.UTF8.GetString(responseBytes, 0, responseLength);
            var bridgeResponse = JsonConvert.DeserializeObject<BridgeResponse>(response);
            return bridgeResponse?.Service.Equals(BridgeServiceName, StringComparison.Ordinal) == true
                ? bridgeResponse
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBlenderRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("blender");
            try
            {
                return processes.Any(process => !process.HasExited);
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static void ShowStartupBridgeGuidance(BlenderDependencyStatus status)
    {
        var bridgeDetail = status.HasStartupBridge
            ? "FModelBlenderBridge.py は見つかりましたが、起動中のBlenderでは受信ブリッジが動いていません。Blenderを完全に終了してから再起動してください。"
            : $"FModelBlenderBridge.py が起動スクリプトにありません。\n\n配置先:\n{status.PreferredStartupScriptPath}\n\nFModelの Resources\\FModelBlenderBridge.py をこの場所へコピーし、Blenderを再起動してください。";
        var ueFormatDetail = status.HasUeFormatInstallation
            ? string.Empty
            : "\n\nUEFormatアドオンも見つかりません。https://github.com/h4lfheart/UEFormat/tree/blender からインストールして有効化してください。";

        ShowGuidance("Blender受信ブリッジが必要です", bridgeDetail + ueFormatDetail);
    }

    private static void ShowUeFormatGuidance()
    {
        ShowGuidance(
            "UEFormatアドオンが必要です",
            "UEFormatのBlenderアドオンが見つからないか、有効になっていません。\n\n" +
            "https://github.com/h4lfheart/UEFormat/tree/blender からインストールし、Blenderの設定で有効化してください。\n" +
            "有効化後はBlenderを再起動してから、もう一度「Blenderに移植」を実行してください。");
    }

    private static void ShowGuidance(string title, string message)
    {
        Log.Warning("{Title}: {Message}", title, message);
        FLogger.Append(ELog.Warning, () => FLogger.Text(message, Constants.WHITE, true));
        Application.Current?.Dispatcher.Invoke(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    private sealed class BridgeResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; init; }

        [JsonProperty("service")]
        public string Service { get; init; }

        [JsonProperty("ueformat_available")]
        public bool? UeFormatAvailable { get; init; }
    }

    private static string ResolveBlenderExecutable()
    {
        var configuredPath = NormalizeBlenderPath(UserSettings.Default.BlenderExecutablePath);
        if (IsBlenderExecutable(configuredPath))
            return Path.GetFullPath(configuredPath);

        foreach (var rawCandidate in EnumerateBlenderCandidates())
        {
            var candidate = NormalizeBlenderPath(rawCandidate);
            if (!IsBlenderExecutable(candidate))
                continue;

            UserSettings.Default.BlenderExecutablePath = Path.GetFullPath(candidate);
            UserSettings.Save();
            return UserSettings.Default.BlenderExecutablePath;
        }

        string selectedPath = null;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Blenderの実行ファイルを選択",
                FileName = "blender.exe",
                DefaultExt = ".exe",
                Filter = "Blender (blender.exe)|blender.exe|実行ファイル (*.exe)|*.exe"
            };

            if (dialog.ShowDialog() == true && IsBlenderExecutable(dialog.FileName))
                selectedPath = Path.GetFullPath(dialog.FileName);
        });

        if (selectedPath is null)
            return null;

        UserSettings.Default.BlenderExecutablePath = selectedPath;
        UserSettings.Save();
        return selectedPath;
    }

    private static IEnumerable<string> EnumerateBlenderCandidates()
    {
        var environmentPath = Environment.GetEnvironmentVariable("BLENDER_PATH");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            yield return environmentPath;

        foreach (var registryHive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            string registryPath = null;
            try
            {
                using var key = registryHive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\blender.exe");
                registryPath = key?.GetValue(null) as string;
            }
            catch
            {
                // Ignore inaccessible registry views and continue with filesystem discovery.
            }

            if (!string.IsNullOrWhiteSpace(registryPath))
                yield return registryPath;
        }

        var installRoots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Blender Foundation")
        };

        foreach (var installRoot in installRoots)
        {
            if (!Directory.Exists(installRoot))
                continue;

            IEnumerable<string> versionDirectories;
            try
            {
                versionDirectories = Directory.EnumerateDirectories(installRoot, "Blender *")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var versionDirectory in versionDirectories)
                yield return Path.Combine(versionDirectory, "blender.exe");
        }

        var steamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Blender", "blender.exe");
        yield return steamPath;

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(directory.Trim('"'), "blender.exe");
    }

    private static bool IsBlenderExecutable(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   Path.GetFileName(path).Equals("blender.exe", StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeBlenderPath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractBridgeScript()
    {
        var dataDirectory = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
        var bridgePath = Path.Combine(dataDirectory, BridgeResourceSuffix);
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(BridgeResourceSuffix, StringComparison.Ordinal));

        if (resourceName is null)
            throw new InvalidOperationException("Blender bridge resource was not found.");

        using var source = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Blender bridge resource could not be opened.");
        using var destination = new FileStream(bridgePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        source.CopyTo(destination);
        return bridgePath;
    }

    private static string WriteRequestFile(string requestJson)
    {
        var requestDirectory = Directory.CreateDirectory(
            Path.Combine(UserSettings.Default.OutputDirectory, ".data", "BlenderRequests")).FullName;
        var requestPath = Path.Combine(requestDirectory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(requestPath, requestJson, new UTF8Encoding(false));
        return requestPath;
    }
}
