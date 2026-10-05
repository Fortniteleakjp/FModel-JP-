using System;
using FModel.Services.Mcp;

namespace FModel;

/// <summary>
/// エントリポイント。<c>--mcp</c> 付きで起動されたときは WPF を起動せず、MCP の stdio 中継として動く
/// (<see cref="McpRelay"/>)。それ以外は従来どおり <see cref="App"/> を起動する。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpRelay.IsRelayInvocation(args))
            return McpRelay.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
