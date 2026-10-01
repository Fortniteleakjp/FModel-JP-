using System.Collections.Generic;

namespace FModel.Services.AssetEditing;

public sealed class AssetEditReport
{
    public List<string> Changes { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>
    /// files written to disk, absolute paths
    /// </summary>
    public List<string> OutputFiles { get; } = [];

    /// <summary>
    /// json paths whose value read back from the written package differs from the edited document
    /// </summary>
    public List<string> VerificationMismatches { get; } = [];

    /// <summary>
    /// values the user didn't touch that read back differently (derived values, or a side effect worth checking)
    /// </summary>
    public List<string> SideEffects { get; } = [];

    public bool Verified { get; set; }

    /// <summary>
    /// the replaced texture as read back from the written package, for the preview
    /// </summary>
    public CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D ReloadedTexture { get; set; }

    /// <summary>
    /// the reshaped mesh as read back from the written package
    /// </summary>
    public CUE4Parse.UE4.Assets.Exports.UObject ReloadedMesh { get; set; }
    public bool HasErrors => Errors.Count > 0;

    public void Change(string path, string what) => Changes.Add($"{path}: {what}");
    public void Warn(string path, string message) => Warnings.Add(string.IsNullOrEmpty(path) ? message : $"{path}: {message}");
    public void Error(string path, string message) => Errors.Add(string.IsNullOrEmpty(path) ? message : $"{path}: {message}");
}
