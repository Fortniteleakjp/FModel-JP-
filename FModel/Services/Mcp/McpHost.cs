using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using FModel.Settings;
using FModel.Views.Resources.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serilog;

namespace FModel.Services.Mcp;

/// <summary>
/// FModel 内蔵 MCP サーバーの起動・停止。
/// <para>
/// MCP クライアント (Claude Desktop / Claude Code / Codex など) は <c>FModel.exe --mcp</c> を stdio サーバーとして起動し、
/// その中継プロセス (<see cref="McpRelay"/>) が名前付きパイプでここに繋ぐ。接続 1 本ごとに公式 C# SDK の
/// <see cref="McpServer"/> を <see cref="StreamServerTransport"/> 上で動かす。
/// </para>
/// <para>
/// パイプはユーザー SID 入りの名前で <see cref="PipeOptions.CurrentUserOnly"/> を付けて作るので、
/// 同じ PC の別ユーザーからは開けない。TCP ポートは開けない (ブラウザからの DNS リバインディング等の心配が無い)。
/// </para>
/// </summary>
public static class McpHost
{
    /// <summary>中継が付ける引数。FModel を MCP サーバーとして使う設定に書くのはこれ</summary>
    public const string RelayArgument = "--mcp";
    /// <summary>中継が FModel を起動したときに付ける引数。この起動に限り設定が無効でもサーバーを立てる</summary>
    public const string LaunchedArgument = "--mcp-launched";
    private const int MaxClients = 8;

    /// <summary>MCP クライアントに登録するコマンド (設定画面に表示)</summary>
    public static string ClientCommand => $"\"{Environment.ProcessPath}\" {RelayArgument}";

    public static string PipeName { get; } = $"FModelJP.Mcp.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    private static readonly object Sync = new();
    private static CancellationTokenSource _cts;
    private static FModelMcpService _service;

    public static bool IsRunning { get; private set; }

    private static bool LaunchedByRelay => Environment.GetCommandLineArgs().Contains(LaunchedArgument, StringComparer.OrdinalIgnoreCase);

    /// <summary>設定 (<see cref="UserSettings.McpServerEnabled"/>) に合わせて起動・停止する。設定変更の監視もここで始める</summary>
    public static void Initialize()
    {
        UserSettings.Default.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UserSettings.McpServerEnabled)) Apply();
        };
        Apply();
    }

    private static void Apply()
    {
        if (UserSettings.Default.McpServerEnabled || LaunchedByRelay) Start();
        else Stop();
    }

    public static void Start()
    {
        lock (Sync)
        {
            if (IsRunning) return;

            _service ??= new FModelMcpService();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _ = AcceptLoopAsync(_cts.Token);
            Log.Information("MCP server listening on pipe {PipeName}", PipeName);
        }
    }

    public static void Stop()
    {
        lock (Sync)
        {
            if (!IsRunning) return;

            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
            Log.Information("MCP server stopped");
        }
    }

    private static async Task AcceptLoopAsync(CancellationToken ct)
    {
        var first = true;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // 最初の 1 本は FirstPipeInstance: 別の FModel が既にサーバーを立てていたら失敗させ、二重起動を知らせる
                var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, options);
                first = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (first)
                {
                    Log.Warning(e, "MCP: another FModel instance is already serving MCP, this one will not");
                    FLogger.Append(ELog.Warning, () => FLogger.Text("Another FModel instance is already serving MCP", Constants.WHITE, true));
                    Stop();
                    return;
                }

                // 同時接続数の上限。誰かが切断するまで待つ
                await Task.Delay(500, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException e)
            {
                Log.Warning(e, "MCP: pipe connection failed");
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(pipe, ct), ct);
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            await using var transport = new StreamServerTransport(pipe, pipe, "fmodel-jp", NullLoggerFactory.Instance);
            await using var server = McpServer.Create(transport, CreateOptions(_service), NullLoggerFactory.Instance);
            Log.Information("MCP client connected");
            FLogger.Append(ELog.Information, () => FLogger.Text("MCP client connected", Constants.WHITE, true));
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // サーバー停止
        }
        catch (Exception e)
        {
            Log.Warning(e, "MCP session ended with an error");
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            Log.Information("MCP client disconnected");
            FLogger.Append(ELog.Information, () => FLogger.Text("MCP client disconnected", Constants.WHITE, true));
        }
    }

    /// <summary>
    /// ツール・リソース・プロンプトを属性付きメソッドから登録した SDK のサーバー設定。
    /// 参考実装は Microsoft.Extensions.Hosting の DI で登録していたが、WPF 本体に Generic Host を持ち込まないよう手で組む。
    /// </summary>
    internal static McpServerOptions CreateOptions(FModelMcpService service)
    {
        var tools = new FModelMcpTools(service);
        var resources = new FModelMcpResources(service);
        var prompts = new FModelMcpPrompts();

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "fmodel-jp", Title = "FModel JP", Version = Constants.APP_VERSION ?? "0.0.0" },
            ServerInstructions = McpWorkflowContent.Guide,
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
            ResourceCollection = new McpServerResourceCollection(),
            PromptCollection = new McpServerPrimitiveCollection<McpServerPrompt>()
        };

        foreach (var method in typeof(FModelMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.IsDefined(typeof(McpServerToolAttribute))))
            options.ToolCollection.Add(McpServerTool.Create(method, tools));
        foreach (var method in typeof(FModelMcpResources).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.IsDefined(typeof(McpServerResourceAttribute))))
            options.ResourceCollection.Add(McpServerResource.Create(method, resources));
        foreach (var method in typeof(FModelMcpPrompts).GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.IsDefined(typeof(McpServerPromptAttribute))))
            options.PromptCollection.Add(McpServerPrompt.Create(method, prompts));

        return options;
    }
}
