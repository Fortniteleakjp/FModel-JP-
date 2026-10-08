using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FModel.Services.Mcp;

// stdin/stdout は AI クライアントの接続期間中維持し、FModel への接続だけを張り直す。
internal static class RelaySession
{
    internal static async Task<int> RunAsync(Stream input, Stream output, string application, bool noLaunch,
        string pipeName, TimeSpan reconnectTimeout)
    {
        using var lifetime = new CancellationTokenSource();
        using var reader = new StreamReader(input, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        var inbox = Channel.CreateBounded<string>(32);
        _ = ReadInputAsync();
        var clientRead = NextInputAsync();
        Connection? connection = null;
        Task<Connection?>? reconnecting = null;
        string? initialize = null;
        string? initialized = null;
        var pending = new Dictionary<string, JsonNode>();

        try
        {
            var connecting = ConnectAsync(pipeName, TimeSpan.FromSeconds(2), null, null, lifetime.Token);
            connection = await connecting;
            if (lifetime.IsCancellationRequested) return 0;
            if (connection is null)
            {
                if (noLaunch)
                {
                    Console.Error.WriteLine("FModel is not running, or its MCP server is disabled (Settings > General > MCP server).");
                    return 2;
                }
                Console.Error.WriteLine("Starting FModel and waiting for its MCP server...");
                Process.Start(new ProcessStartInfo(application, McpConnectionInfo.LaunchedArgument) { UseShellExecute = true });
                connecting = ConnectAsync(pipeName, reconnectTimeout, null, null, lifetime.Token);
                connection = await connecting;
                if (lifetime.IsCancellationRequested) return 0;
                if (connection is null) return 3;
            }

            Console.Error.WriteLine("Connected to FModel MCP server.");
            Task<string?>? serverRead = connection.Reader.ReadLineAsync(lifetime.Token).AsTask();
            while (true)
            {
                var backendEvent = (Task?) serverRead ?? reconnecting!;
                await Task.WhenAny(clientRead, backendEvent);
                if (lifetime.IsCancellationRequested) return 0;
                // 本体停止とクライアント要求が同時なら、先に切断を処理する。
                if (backendEvent.IsCompleted)
                {
                    if (reconnecting is not null)
                    {
                        connection = await reconnecting;
                        reconnecting = null;
                        if (connection is null)
                        {
                            Console.Error.WriteLine("Timed out waiting for FModel to restart.");
                            return 3;
                        }
                        Console.Error.WriteLine("Reconnected to FModel MCP server. Previous jobs and extra sessions are no longer available.");
                        serverRead = connection.Reader.ReadLineAsync(lifetime.Token).AsTask();
                        continue;
                    }

                    string? line;
                    try { line = await serverRead!; }
                    catch (IOException) { line = null; }
                    if (line is not null)
                    {
                        var message = JsonNode.Parse(line)!.AsObject();
                        if (message["method"] is null && message["id"] is { } responseId)
                            pending.Remove(responseId.ToJsonString());
                        await writer.WriteLineAsync(line);
                        serverRead = connection!.Reader.ReadLineAsync(lifetime.Token).AsTask();
                        continue;
                    }
                    await DisconnectAsync();
                    continue;
                }

                var requestLine = await clientRead;
                if (requestLine is null) return 0;
                clientRead = NextInputAsync();
                var request = JsonNode.Parse(requestLine)!.AsObject();
                var method = request["method"]?.GetValue<string>();
                if (method == "initialize") initialize = requestLine;
                if (method == "notifications/initialized") initialized = requestLine;
                var id = request["id"];
                if (connection is null)
                {
                    if (method is not null && id is not null)
                        await WriteErrorAsync(id, "FModel is restarting. Retry after it has restarted; previous jobs and extra sessions must be recreated.");
                    continue;
                }
                if (method is not null && id is not null) pending[id.ToJsonString()] = id.DeepClone();
                try { await connection.Writer.WriteLineAsync(requestLine.AsMemory(), lifetime.Token); }
                catch (IOException) { await DisconnectAsync(); }
            }

            async Task DisconnectAsync()
            {
                connection?.Dispose();
                connection = null;
                serverRead = null;
                foreach (var id in pending.Values)
                    await WriteErrorAsync(id, "FModel disconnected. This request was interrupted and was not retried. Previous jobs and extra sessions must be recreated.");
                pending.Clear();
                Console.Error.WriteLine("FModel disconnected; waiting for it to restart...");
                // 更新プログラムが再起動するので、ここでは旧 EXE を自動起動しない。
                reconnecting = ConnectAsync(pipeName, reconnectTimeout, initialize, initialized, lifetime.Token);
            }

            Task WriteErrorAsync(JsonNode id, string message) => writer.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message }
            }.ToJsonString());

        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        finally
        {
            await lifetime.CancelAsync();
            connection?.Dispose();
            if (reconnecting is not null) (await reconnecting)?.Dispose();
        }

        async Task ReadInputAsync()
        {
            try
            {
                while (await reader.ReadLineAsync(lifetime.Token) is { } line)
                    await inbox.Writer.WriteAsync(line, lifetime.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally
            {
                inbox.Writer.TryComplete();
                try { await lifetime.CancelAsync(); }
                catch (ObjectDisposedException) { /* 中継が既に終了した */ }
            }
        }

        async Task<string?> NextInputAsync() => await inbox.Reader.WaitToReadAsync()
            ? await inbox.Reader.ReadAsync() : null;
    }

    private static async Task<Connection?> ConnectAsync(string pipeName, TimeSpan timeout, string? initialize,
        string? initialized, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            var connection = new Connection(pipeName);
            try
            {
                await connection.Pipe.ConnectAsync(500, deadline.Token);
                connection.InitializeStreams();
                if (initialize is not null)
                {
                    await connection.Writer.WriteLineAsync(initialize.AsMemory(), deadline.Token);
                    // 初期化応答を AI 側に二重送信しない。新ホストとの交渉を内部で完了する。
                    var response = await connection.Reader.ReadLineAsync(deadline.Token);
                    if (response is null || JsonNode.Parse(response)?["result"] is null)
                        throw new IOException("FModel MCP initialization failed.");
                    if (initialized is not null)
                        await connection.Writer.WriteLineAsync(initialized.AsMemory(), deadline.Token);
                }
                return connection;
            }
            catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
            {
                connection.Dispose();
                if (deadline.IsCancellationRequested) break;
                try { await Task.Delay(100, deadline.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        return null;
    }

    private sealed class Connection : IDisposable
    {
        public NamedPipeClientStream Pipe { get; }
        public StreamReader Reader { get; private set; } = null!;
        public StreamWriter Writer { get; private set; } = null!;

        public Connection(string pipeName)
        {
            Pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        public void InitializeStreams()
        {
            Reader = new StreamReader(Pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            Writer = new StreamWriter(Pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        }

        public void Dispose()
        {
            // 切断済みのパイプへ StreamWriter.Dispose が flush しないよう、先にパイプを閉じる。
            Pipe.Dispose();
            Reader?.Dispose();
            try { Writer?.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }
}
