using FModel.Services.Mcp;

if (args.Contains("--help"))
{
    Console.Error.WriteLine("FModel.Mcp.exe --application <FModel.exe> [--no-launch]");
    return 0;
}

var applicationIndex = Array.IndexOf(args, "--application");
if (applicationIndex < 0 || applicationIndex + 1 >= args.Length || !Path.IsPathFullyQualified(args[applicationIndex + 1]))
{
    Console.Error.WriteLine("Specify --application with the full path to FModel.exe.");
    return 1;
}

try
{
    return await RelaySession.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(),
        args[applicationIndex + 1], args.Contains("--no-launch"), McpConnectionInfo.PipeName, TimeSpan.FromMinutes(3));
}
catch (Exception e)
{
    Console.Error.WriteLine($"FModel MCP relay failed: {e.Message}");
    return 1;
}
