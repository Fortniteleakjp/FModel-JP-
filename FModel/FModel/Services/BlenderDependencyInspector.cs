using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FModel.Services;

internal static class BlenderDependencyInspector
{
    private const string BridgeScriptName = "FModelBlenderBridge.py";
    private const string UeFormatAddonId = "io_scene_ueformat";

    public static BlenderDependencyStatus Inspect()
    {
        var blenderRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Blender Foundation", "Blender");
        var versionDirectories = EnumerateDirectories(blenderRoot).ToArray();
        var bridgeScripts = new List<string>();
        var ueFormatLocations = new List<string>();

        foreach (var versionDirectory in versionDirectories)
        {
            var bridgeScript = Path.Combine(versionDirectory, "scripts", "startup", BridgeScriptName);
            if (File.Exists(bridgeScript))
                bridgeScripts.Add(bridgeScript);

            foreach (var location in EnumerateUeFormatLocations(versionDirectory))
                ueFormatLocations.Add(location);
        }

        var preferredVersionDirectory = versionDirectories
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var preferredStartupScriptPath = preferredVersionDirectory is null
            ? Path.Combine(blenderRoot, "<Blenderのバージョン>", "scripts", "startup", BridgeScriptName)
            : Path.Combine(preferredVersionDirectory, "scripts", "startup", BridgeScriptName);

        return new BlenderDependencyStatus(
            preferredStartupScriptPath,
            bridgeScripts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ueFormatLocations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).ToArray()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateUeFormatLocations(string versionDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(versionDirectory, "scripts", "addons", UeFormatAddonId),
            Path.Combine(versionDirectory, "scripts", "addons", $"{UeFormatAddonId}.py"),
            Path.Combine(versionDirectory, "extensions", "user_default", UeFormatAddonId),
            Path.Combine(versionDirectory, "extensions", "blender_org", UeFormatAddonId)
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) || File.Exists(candidate))
                yield return candidate;
        }

        var extensionDirectory = Path.Combine(versionDirectory, "extensions");
        if (!Directory.Exists(extensionDirectory))
            yield break;

        IEnumerable<string> extensionCandidates;
        try
        {
            extensionCandidates = Directory.EnumerateDirectories(
                extensionDirectory, UeFormatAddonId, SearchOption.AllDirectories).ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var candidate in extensionCandidates)
            yield return candidate;
    }
}

internal sealed class BlenderDependencyStatus(
    string preferredStartupScriptPath,
    IReadOnlyList<string> bridgeScripts,
    IReadOnlyList<string> ueFormatLocations)
{
    public string PreferredStartupScriptPath { get; } = preferredStartupScriptPath;
    public IReadOnlyList<string> BridgeScripts { get; } = bridgeScripts;
    public IReadOnlyList<string> UeFormatLocations { get; } = ueFormatLocations;
    public bool HasStartupBridge => BridgeScripts.Count > 0;
    public bool HasUeFormatInstallation => UeFormatLocations.Count > 0;
}
