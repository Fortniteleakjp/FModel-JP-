using System;
using System.Collections.Generic;
using System.Linq;
using FModel.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Serilog;

namespace FModel.Services;

/// <summary>
/// Writes the user settings to the log, a snapshot at startup and what changed each time the settings window closes,
/// so a log sent with a bug report tells which options were on.
/// Logs get shared: keys, tokens and credentials are hidden and the user profile folder is taken out of paths.
/// </summary>
public static class SettingsLog
{
    // the whole map of game directories repeats CurrentDir, the rest is personal or noise
    private static readonly string[] _skipped = ["PerDirectory", "LastAuthResponse", "Manual", "LastUpdateCheck", "NextUpdateCheck", "LastSeenReleaseNotes"];

    private static readonly string[] _secretWords = ["Key", "Token", "Auth", "Password", "Secret", "Cookie", "Signature"];

    /// <summary>Settings worth knowing when a report comes in, logged once at startup.</summary>
    private static readonly string[] _startupKeys =
    [
        "InterfaceLanguage", "AssetLanguage", "LoadingMode", "AesReload", "ReadScriptData", "ReadShaderMaps",
        "MaxExportPerPage", "MergeEditorOnlyDataExports", "KeepDirectoryStructure", "ShowDecompileOption",
        "FeaturePreviewNewAssetExplorer", "PreviewTexturesAssetExplorer", "ExplorerViewMode", "ConvertAudioOnBulkExport",
        "DecompileLua", "MeshExportFormat", "TextureExportFormat", "MaterialExportFormat",
        "CurrentDir.UeVersion", "CurrentDir.TexturePlatform"
    ];

    private static readonly string _userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Every setting as "Path.To.Setting" -> value, secrets hidden.</summary>
    public static Dictionary<string, string> Snapshot()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (UserSettings.Default == null) return values;

            var root = JObject.Parse(JsonConvert.SerializeObject(UserSettings.Default, new StringEnumConverter()));
            foreach (var property in root.Properties())
            {
                if (_skipped.Contains(property.Name)) continue;
                Flatten(property.Name, property.Value, values);
            }

            // the settings of the game directory in use are not serialized with the rest (JsonIgnore)
            if (UserSettings.Default.CurrentDir != null)
                Flatten("CurrentDir", JObject.Parse(JsonConvert.SerializeObject(UserSettings.Default.CurrentDir, new StringEnumConverter())), values);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not read the settings for the log");
        }

        return values;
    }

    public static void LogStartup()
    {
        var snapshot = Snapshot();
        var pairs = _startupKeys.Where(snapshot.ContainsKey).Select(key => $"{key}={snapshot[key]}");
        Log.Information("Settings: {Settings}", string.Join(", ", pairs));
    }

    /// <summary>Logs every setting whose value differs between the two snapshots.</summary>
    public static void LogChanges(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, string origin)
    {
        var changes = 0;
        foreach (var key in before.Keys.Union(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var oldValue);
            after.TryGetValue(key, out var newValue);
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;

            Log.Information("Setting changed ({Origin}): {Setting}: {Old} -> {New}", origin, key, oldValue ?? "(none)", newValue ?? "(none)");
            changes++;
        }

        if (changes == 0) Log.Information("Settings window closed ({Origin}), nothing changed", origin);
    }

    private static void Flatten(string path, JToken token, Dictionary<string, string> values)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                    Flatten($"{path}.{property.Name}", property.Value, values);
                break;
            case JArray array:
                values[path] = IsSecret(path) ? Hidden(array.ToString(Formatting.None)) : Redact(array.ToString(Formatting.None));
                break;
            default:
                var text = token.Type switch
                {
                    JTokenType.Null => "null",
                    JTokenType.String => (string) token,
                    _ => token.ToString(Formatting.None)
                };
                values[path] = IsSecret(path) ? Hidden(text) : Redact(text);
                break;
        }
    }

    private static bool IsSecret(string path)
    {
        var name = path[(path.LastIndexOf('.') + 1)..];
        if (name == "Key") return false; // the key of a hotkey (DirLeftTab.Key), not a secret

        return _secretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
               path.Contains("Endpoints", StringComparison.OrdinalIgnoreCase); // endpoint urls and headers may carry tokens
    }

    // a secret is never written, only whether it is set, the change log still shows that it changed
    private static string Hidden(string value) =>
        string.IsNullOrEmpty(value) || value is "null" or "[]" or "{}" ? "(empty)" : $"(hidden #{(uint) value.GetHashCode():X8})";

    private static string Redact(string value) =>
        string.IsNullOrEmpty(_userProfile) ? value : value.Replace(_userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(_userProfile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(_userProfile.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
}
