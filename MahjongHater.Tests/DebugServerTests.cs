using MahjongHater.Core;
using Xunit;

namespace MahjongHater.Tests;

public class DebugServerTests
{
    private static DebugServer StartServer(out int port)
    {
        // Pick a free port by binding to 0 first.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var server = new DebugServer(port, (path, query) =>
        {
            object? result = path switch
            {
                "/json" => new Dictionary<string, object?> { ["hello"] = "world", ["n"] = 42 },
                "/text" => new List<string> { "line1", "line2" },
                "/echo" => query,
                _ => null,
            };
            return Task.FromResult(result);
        });
        server.Start();
        return server;
    }

    [Fact]
    public async Task Serves_json_text_query_and_404()
    {
        using var server = StartServer(out var port);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var json = await client.GetAsync("/json");
        Assert.True(json.IsSuccessStatusCode);
        Assert.Contains("application/json", json.Content.Headers.ContentType!.ToString());
        Assert.Contains("\"hello\": \"world\"", await json.Content.ReadAsStringAsync());

        var text = await client.GetAsync("/text");
        Assert.Contains("text/plain", text.Content.Headers.ContentType!.ToString());
        Assert.Equal("line1\nline2", await text.Content.ReadAsStringAsync());

        var echo = await client.GetStringAsync("/echo?node=133&x=a%20b");
        Assert.Contains("\"node\": \"133\"", echo);
        Assert.Contains("\"x\": \"a b\"", echo);

        var missing = await client.GetAsync("/nope");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Stop_releases_the_port()
    {
        var server = StartServer(out var port);
        server.Dispose();

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("/json"));

        // Port is reusable immediately.
        using var second = StartServer(out _);
    }
}
