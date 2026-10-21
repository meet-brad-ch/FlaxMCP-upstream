using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FlaxMcpBridge.Host;
using Xunit;

namespace FlaxMcp.Tests.Bridge;

public sealed class BridgeHostServerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "FlaxMcpHostTests_" + Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "FlaxMcpHost-" + Guid.NewGuid().ToString("N");
    private readonly BridgeHostServer _host;

    public BridgeHostServerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _host = new BridgeHostServer(new BridgeHostOptions(
            _tempDir,
            _pipeName,
            Path.Combine(_tempDir, "sessions", "handshake.json"),
            "testversion",
            TimeSpan.FromMilliseconds(500)
        ));
        _host.Start();
    }

    public void Dispose()
    {
        _host.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Request_WithAttachedPlugin_IsAnsweredByThePlugin()
    {
        _host.Attach(Plugin(1, line => Task.FromResult("{\"id\":7,\"result\":{\"echo\":true}}")));

        var response = await AskAsync("{\"id\":7,\"method\":\"ping\"}");

        Assert.Equal("{\"id\":7,\"result\":{\"echo\":true}}", response);
    }

    [Fact]
    public async Task HostStatus_WithoutPlugin_IsAnsweredByTheHost()
    {
        using var response = JsonDocument.Parse(await AskAsync("{\"id\":3,\"method\":\"host_status\"}"));

        var result = response.RootElement.GetProperty("result");
        Assert.Equal(3, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("testversion", result.GetProperty("hostVersion").GetString());
        Assert.False(result.GetProperty("attached").GetBoolean());
        Assert.True(result.GetProperty("accepting").GetBoolean());
    }

    [Fact]
    public async Task Request_WhileDetached_WaitsForThePluginToAttach()
    {
        var answer = AskAsync("{\"id\":1,\"method\":\"scene_graph\"}");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _host.Attach(Plugin(2, line => Task.FromResult("{\"id\":1,\"result\":{}}")));

        Assert.Equal("{\"id\":1,\"result\":{}}", await answer);
    }

    [Fact]
    public async Task Request_WithNoPluginWithinTheWait_FailsWithPluginNotAttached()
    {
        var error = ErrorCode(await AskAsync("{\"id\":1,\"method\":\"scene_graph\"}"));

        Assert.Equal(BridgeHostServer.PluginNotAttached, error);
    }

    [Fact]
    public async Task Request_InFlightWhenThePluginDetaches_FailsWithInterruptedByReload()
    {
        var never = new TaskCompletionSource<string>();
        _host.Attach(Plugin(5, line => never.Task));
        var answer = AskAsync("{\"id\":9,\"method\":\"execute_csharp\"}");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        _host.Detach(5);

        using var response = JsonDocument.Parse(await answer);
        Assert.Equal(9, response.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(BridgeHostServer.InterruptedByReload, response.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Detach_OfAnOlderGeneration_KeepsTheCurrentPlugin()
    {
        _host.Attach(Plugin(1, line => Task.FromResult("{\"id\":1,\"result\":\"one\"}")));
        _host.Attach(Plugin(2, line => Task.FromResult("{\"id\":1,\"result\":\"two\"}")));

        _host.Detach(1);

        Assert.Equal("{\"id\":1,\"result\":\"two\"}", await AskAsync("{\"id\":1,\"method\":\"ping\"}"));
    }

    [Fact]
    public async Task Request_WhenThePluginThrows_FailsWithRequestFailed()
    {
        _host.Attach(Plugin(1, line => Task.FromException<string>(new InvalidOperationException("broken"))));

        var response = await AskAsync("{\"id\":4,\"method\":\"ping\"}");

        Assert.Equal("request_failed", ErrorCode(response));
        Assert.Contains("broken", response);
    }

    [Fact]
    public async Task Request_ThatIsNotJson_FailsWithInvalidRequest()
    {
        Assert.Equal("invalid_request", ErrorCode(await AskAsync("not json")));
    }

    [Fact]
    public async Task Requests_FromTwoClientsAtOnce_AreBothAnswered()
    {
        var release = new TaskCompletionSource<string>();
        _host.Attach(Plugin(1, line => line.Contains("slow") ? release.Task : Task.FromResult("{\"id\":2,\"result\":\"fast\"}")));
        var slow = AskAsync("{\"id\":1,\"method\":\"slow\"}");

        var fast = await AskAsync("{\"id\":2,\"method\":\"fast\"}");
        release.SetResult("{\"id\":1,\"result\":\"slow\"}");

        Assert.Equal("{\"id\":2,\"result\":\"fast\"}", fast);
        Assert.Equal("{\"id\":1,\"result\":\"slow\"}", await slow);
    }

    [Fact]
    public void Attach_WritesTheHandshakeAndDisposeRemovesIt()
    {
        var path = Path.Combine(_tempDir, "sessions", "handshake.json");

        _host.Attach(Plugin(42, line => Task.FromResult("")));

        using (var handshake = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var root = handshake.RootElement;
            Assert.Equal(_pipeName, root.GetProperty("pipeName").GetString());
            Assert.Equal(16, root.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(42, root.GetProperty("generation").GetInt64());
            Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
            Assert.Equal("testversion", root.GetProperty("hostVersion").GetString());
        }

        _host.Dispose();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void PipeNameFor_MatchesTheServersDerivation()
    {
        Assert.Equal(
            BridgeHostServer.PipeNameFor("C:/Projects/Game/"),
            BridgeHostServer.PipeNameFor("c:\\projects\\game")
        );
        Assert.StartsWith("FlaxMcp-", BridgeHostServer.PipeNameFor("C:/Projects/Game"));
    }

    private static BridgeHostAttachment Plugin(long generation, Func<string, Task<string>> dispatch) =>
        new(generation, 16, "1.0.0", 6912, dispatch);

    private static string? ErrorCode(string response)
    {
        using var document = JsonDocument.Parse(response);
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private async Task<string> AskAsync(string line)
    {
        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(line);
        return await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? throw new IOException("The host closed the pipe.");
    }
}
