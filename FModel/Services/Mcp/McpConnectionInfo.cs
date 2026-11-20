using System;
using System.Security.Principal;

namespace FModel.Services.Mcp;

public static class McpConnectionInfo
{
    public const string RelayArgument = "--mcp";
    public const string LaunchedArgument = "--mcp-launched";

    /// <summary>中継が FModel を起動してよいことを示す引数。既定では起動しない（AI クライアントの自動起動に連動して開くため）。</summary>
    public const string LaunchArgument = "--launch";
    /// <summary>旧既定（起動する）の頃の無効化引数。既定が起動しないになったため、互換のため受け付けて優先するだけ。</summary>
    public const string NoLaunchArgument = "--no-launch";

    public const string NotRunningMessage = "FModel is not running, or its MCP server is disabled (Settings > General > MCP server). " +
                                            "Start FModel first, or add --launch to let the relay start it.";

    public static bool ShouldLaunch(string[] args) =>
        Array.Exists(args, a => a.Equals(LaunchArgument, StringComparison.OrdinalIgnoreCase)) &&
        !Array.Exists(args, a => a.Equals(NoLaunchArgument, StringComparison.OrdinalIgnoreCase));

    public static string PipeName { get; } = $"FModelJP.Mcp.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";
}
