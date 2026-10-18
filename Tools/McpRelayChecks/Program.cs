using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using FModel.Services.Mcp;

var tests = new (string Name, Func<Task> Run)[]
{
    ("再接続・再初期化・中断要求の非再実行", CheckReconnectAsync),
    ("再接続待機中のクライアント終了", CheckClientExitAsync),
    ("初回接続待機中のクライアント終了", CheckInitialExitAsync),
    ("no-launch・接続タイムアウト", CheckNoLaunchAsync),
    ("再初期化失敗時の再試行", CheckFailedHandshakeAsync)
};
var failed = 0;
foreach (var (name, run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(15)); Console.WriteLine($"合格: {name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"失敗: {name}\n{e}"); }
}
Console.WriteLine($"合格 {tests.Length - failed}/{tests.Length}");
return failed == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task CheckReconnectAsync()
{
    await using var client = await Client.CreateAsync();
    using var oldHost = new Host(client.PipeName);
    var relay = client.Start();
    await oldHost.AcceptAsync();
    await InitializeAsync(client, oldHost);

    // 実行途中の書き込み要求を、新ホストに勝手に再送しない。
    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":\"export\",\"method\":\"tools/call\",\"params\":{\"name\":\"fmodel_start_export\"}}");
    Require((await oldHost.ReadAsync())["id"]!.GetValue<string>() == "export", "要求が届きません");
    oldHost.Dispose();
    var interrupted = await client.ReadAsync();
    Require(interrupted["id"]!.GetValue<string>() == "export" && interrupted["error"] is not null, "中断が通知されません");
    Require(!relay.IsCompleted, "本体終了で中継が終了しました");

    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
    Require((await client.ReadAsync())["error"] is not null, "再起動中の要求が待ち続けます");
    using var newHost = new Host(client.PipeName);
    await newHost.AcceptAsync();
    Require((await newHost.ReadAsync())["method"]!.GetValue<string>() == "initialize", "新ホストを初期化していません");
    await newHost.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"new\",\"version\":\"2\"}}}");
    Require((await newHost.ReadAsync())["method"]!.GetValue<string>() == "notifications/initialized", "初期化通知を復元していません");

    // initializedの到着と中継側の接続切り替えは別タスクなので、転送を確認して同期する。
    await newHost.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
    Require((await client.ReadAsync())["method"]!.GetValue<string>() == "notifications/tools/list_changed", "再接続後の通知が届きません");

    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\"}");
    Require((await newHost.ReadAsync())["id"]!.GetValue<int>() == 3, "古い要求が再送されたか、新要求が届きません");
    await newHost.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{\"tools\":[]}}");
    Require((await client.ReadAsync())["id"]!.GetValue<int>() == 3, "初期化応答が二重送信されたか、要求が戻りません");
    await client.CloseInputAsync();
    Require(await relay == 0, "クライアント終了時の終了コードが違います");
}

static async Task CheckClientExitAsync()
{
    await using var client = await Client.CreateAsync();
    using var host = new Host(client.PipeName);
    var relay = client.Start();
    await host.AcceptAsync();
    await InitializeAsync(client, host);
    host.Dispose();
    // 切断を処理したことを、エラー応答で確認してから stdin を閉じる。
    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/list\"}");
    Require((await client.ReadAsync())["error"] is not null, "切断が処理されません");
    await client.CloseInputAsync();
    Require(await relay.WaitAsync(TimeSpan.FromSeconds(2)) == 0, "再接続待機が終了しません");
}

static async Task CheckInitialExitAsync()
{
    await using var client = await Client.CreateAsync();
    var relay = client.Start();
    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
    await client.CloseInputAsync();
    Require(await relay.WaitAsync(TimeSpan.FromSeconds(2)) == 0, "初回接続待機が終了しません");
}

static async Task CheckNoLaunchAsync()
{
    await using (var missing = await Client.CreateAsync())
        Require(await missing.Start() == 2, "no-launch でアプリを起動したか、終了コードが違います");

    await using var client = await Client.CreateAsync();
    using var host = new Host(client.PipeName);
    var relay = client.Start(TimeSpan.FromMilliseconds(300));
    await host.AcceptAsync();
    await InitializeAsync(client, host);
    host.Dispose();
    Require(await relay.WaitAsync(TimeSpan.FromSeconds(2)) == 3, "再接続のタイムアウトが働きません");
}

static async Task CheckFailedHandshakeAsync()
{
    await using var client = await Client.CreateAsync();
    using var host = new Host(client.PipeName);
    var relay = client.Start();
    await host.AcceptAsync();
    await InitializeAsync(client, host);
    host.Dispose();
    using (var failingHost = new Host(client.PipeName))
    {
        await failingHost.AcceptAsync();
        await failingHost.ReadAsync();
        await failingHost.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32603,\"message\":\"not ready\"}}");
    }
    using var readyHost = new Host(client.PipeName);
    await readyHost.AcceptAsync();
    Require((await readyHost.ReadAsync())["method"]!.GetValue<string>() == "initialize", "初期化を再試行しません");
    await readyHost.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
    await readyHost.ReadAsync();
    await client.CloseInputAsync();
    Require(await relay == 0, "正常に終了しません");
}

static async Task InitializeAsync(Client client, Host host)
{
    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"check\",\"version\":\"1\"}}}");
    await host.ReadAsync();
    await host.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}");
    await client.ReadAsync();
    await client.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    await host.ReadAsync();
}

sealed class Host : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    public Host(string name)
    {
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true);
    }
    public async Task AcceptAsync() { await _pipe.WaitForConnectionAsync(); _writer.AutoFlush = true; }
    public async Task<JsonNode> ReadAsync() => JsonNode.Parse(await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new IOException("EOF"))!;
    public Task WriteAsync(string value) => _writer.WriteLineAsync(value);
    public void Dispose() => _pipe.Dispose();
}

sealed class Client : IAsyncDisposable
{
    public string PipeName { get; } = "FModelJP.RelayCheck." + Guid.NewGuid().ToString("N");
    private readonly string _inputName = "FModelJP.Input." + Guid.NewGuid().ToString("N");
    private readonly string _outputName = "FModelJP.Output." + Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream _input;
    private readonly NamedPipeServerStream _output;
    private NamedPipeClientStream _relayInput = null!;
    private NamedPipeClientStream _relayOutput = null!;
    private StreamWriter _writer = null!;
    private StreamReader _reader = null!;

    private Client()
    {
        _input = new NamedPipeServerStream(_inputName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _output = new NamedPipeServerStream(_outputName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public static async Task<Client> CreateAsync()
    {
        var client = new Client();
        await client.ConnectStreamsAsync();
        return client;
    }
    private async Task ConnectStreamsAsync()
    {
        _relayInput = new NamedPipeClientStream(".", _inputName, PipeDirection.InOut, PipeOptions.Asynchronous);
        _relayOutput = new NamedPipeClientStream(".", _outputName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Task.WhenAll(_relayInput.ConnectAsync(1000), _input.WaitForConnectionAsync(timeout.Token),
            _relayOutput.ConnectAsync(1000), _output.WaitForConnectionAsync(timeout.Token));
        _writer = new StreamWriter(_input, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_output, new UTF8Encoding(false), leaveOpen: true);
    }
    public Task<int> Start(TimeSpan? timeout = null) => RelaySession.RunAsync(_relayInput, _relayOutput,
        @"Z:\missing-fmodel.exe", true, PipeName, timeout ?? TimeSpan.FromSeconds(10));
    public Task WriteAsync(string value) => _writer.WriteLineAsync(value);
    public async Task<JsonNode> ReadAsync() => JsonNode.Parse(await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new IOException("EOF"))!;
    public async Task CloseInputAsync() { await _writer.FlushAsync(); _input.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        await _input.DisposeAsync();
        await _output.DisposeAsync();
        await _relayInput.DisposeAsync();
        await _relayOutput.DisposeAsync();
    }
}
