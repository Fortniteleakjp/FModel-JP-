using System;
using System.Security.Principal;

namespace FModel.Services.Mcp;

public static class McpConnectionInfo
{
    public const string RelayArgument = "--mcp";
    public const string LaunchedArgument = "--mcp-launched";
    public static string PipeName { get; } = $"FModelJP.Mcp.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";
}
