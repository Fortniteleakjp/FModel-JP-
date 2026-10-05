using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FModel.Services.Mcp;

/// <summary>
/// <c>FModel.exe --mcp</c> で起動される stdio ⇔ 名前付きパイプの中継。
/// <para>
/// MCP クライアントは stdio サーバーとしてこのプロセスを起動する。中継は起動中の FModel の
/// <see cref="McpHost"/> に繋ぎ、stdin → パイプ、パイプ → stdout をバイト列のまま流すだけ
/// (JSON-RPC の中身は FModel 側の SDK が処理する)。WPF は一切起動しない。
/// </para>
/// <para>
/// FModel が起動していなければ <see cref="McpHost.LaunchedArgument"/> を付けて起動し、パイプが現れるまで待つ。
/// 診断メッセージはすべて stderr に出す (stdout は MCP のプロトコル専用)。
/// </para>
/// </summary>
public static class McpRelay
{
    private static readonly TimeSpan QuickConnect = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromMinutes(3);

    public static bool IsRelayInvocation(string[] args) => args.Any(a => a.Equals(McpHost.RelayArgument, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.Error.WriteLine("FModel.exe --mcp [--no-launch]\nRelays an MCP stdio client to the MCP server running inside FModel (enable it in Settings > General).");
            return 0;
        }

        try
        {
            return RunAsync(args.Any(a => a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase))).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FModel MCP relay failed: {e.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(bool noLaunch)
    {
        // CurrentUserOnly: サーバー側が同じユーザーで動いていることも確かめる (パイプ名の横取り対策)
        await using var pipe = new NamedPipeClientStream(".", McpHost.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        if (!await TryConnectAsync(pipe, QuickConnect))
        {
            if (noLaunch)
            {
                Console.Error.WriteLine("FModel is not running, or its MCP server is disabled (Settings > General > MCP server).");
                return 2;
            }

            Console.Error.WriteLine("FModel is not running; starting it and waiting for its MCP server...");
            // ShellExecute で起動して stdio ハンドルを継承させない。継承すると FModel のコンソールログが
            // MCP クライアントの stdout に混ざり、プロトコルが壊れる
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, McpHost.LaunchedArgument) { UseShellExecute = true });
            if (!await TryConnectAsync(pipe, LaunchTimeout))
            {
                Console.Error.WriteLine("Timed out waiting for FModel's MCP server. If FModel is already running, enable Settings > General > MCP server.");
                return 3;
            }
        }

        Console.Error.WriteLine($"Connected to FModel MCP server ({McpHost.PipeName}).");
        await using var stdin = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();

        using var done = new CancellationTokenSource();
        var upstream = PumpAsync(stdin, pipe, done.Token);      // クライアント → FModel
        var downstream = PumpAsync(pipe, stdout, done.Token);   // FModel → クライアント

        // どちらかが閉じたら (クライアント終了 / FModel 終了) 中継も終える
        await Task.WhenAny(upstream, downstream);
        await done.CancelAsync();
        return 0;
    }

    private static async Task<bool> TryConnectAsync(NamedPipeClientStream pipe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None);
                return true;
            }
            catch (TimeoutException)
            {
                // パイプがまだ無い / 全インスタンス使用中
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
        }

        return false;
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, ct)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), ct);
                await to.FlushAsync(ct);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 片側が閉じた
        }
    }
}
