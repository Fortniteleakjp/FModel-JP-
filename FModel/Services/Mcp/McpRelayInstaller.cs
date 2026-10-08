using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace FModel.Services.Mcp;

internal static class McpRelayInstaller
{
    private static readonly Lazy<string> Command = new(Install);
    public static string ClientCommand => Command.Value;

    private static string Install() => Install(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FModelJP", "Mcp"),
        Environment.ProcessPath);

    private static string Install(string cacheDirectory, string application)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FModel.Mcp.exe")
                             ?? throw new InvalidOperationException("The MCP relay was not embedded in FModel.");
        var hash = Convert.ToHexString(SHA256.HashData(resource));
        resource.Position = 0;
        // 実行中の中継を上書きせず、更新 ZIP の展開先からも分離する。
        var directory = Path.Combine(cacheDirectory, hash);
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "FModel.Mcp.exe");
        if (!File.Exists(executable))
        {
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = File.Create(temporary)) resource.CopyTo(file);
                try { File.Move(temporary, executable); }
                catch (IOException) when (File.Exists(executable)) { /* 別の FModel が先に配置した */ }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return $"\"{executable}\" --application \"{application}\"";
    }
}
