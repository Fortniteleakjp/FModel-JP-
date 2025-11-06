using System;
using System.ComponentModel;

namespace FModel;

public enum EBuildKind
{
    Debug,
    Release,
    Unknown
}

public enum EErrorKind
{
    Ignore,
    Restart,
    ResetSettings
}

public enum SettingsOut
{
    ReloadLocres,
    ReloadMappings
}

public enum EStatusKind
{
    Ready, // ready
    Loading, // doing stuff
    Stopping, // trying to stop
    Stopped, // stopped
    Failed, // crashed
    Completed // worked
}

public enum EAesReload
{
    [Description("起動時")]
    Always,
    [Description("しない")]
    Never,
    [Description("１日に１回")]
    OncePerDay
}

public enum EDiscordRpc
{
    [Description("常に表示")]
    Always,
    [Description("常に表示しない")]
    Never
}

public enum ELoadingMode
{
    [Description("Multiple")]
    Multiple,
    [Description("すべてのファイル")]
    All,
    [Description("追加・移動したファイル")]
    AllButNew,
    [Description("更新されたファイル")]
    AllButModified,
    [Description("すべて（パッチ適用済みアセットを除く）")]
    AllButPatched,
}

// public enum EUpdateMode
// {
//     [Description("Stable")]
//     Stable,
//     [Description("Beta")]
//     Beta,
//     [Description("QA Testing")]
//     Qa
// }

public enum ECompressedAudio
{
    [Description("解凍して再生")]
    PlayDecompressed,
    [Description("圧縮されたまま再生 (再生できない場合があります)")]
    PlayCompressed
}

public enum EIconStyle
{
    [Description("Default")]
    Default,
    [Description("No Background")]
    NoBackground,
    [Description("No Text")]
    NoText,
    [Description("Flat")]
    Flat,
    [Description("Cataba")]
    Cataba,
    // [Description("Community")]
    // CommunityMade
}

public enum EEndpointType
{
    Aes,
    Mapping
}

[Flags]
public enum EBulkType
{
    None =          0,
    Auto =          1 << 0,
    Properties =    1 << 1,
    Textures =      1 << 2,
    Meshes =        1 << 3,
    Skeletons =     1 << 4,
    Animations =    1 << 5,
    Audio =         1 << 6
}
