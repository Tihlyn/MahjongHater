using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MahjongHater.Core;

// Minimal localhost-only HTTP debug endpoint for live, step-by-step inspection of the
// plugin's view of the game (call → observe → play → call again).
//
// Hand-rolled on TcpListener instead of HttpListener so no Windows URL ACL is needed.
// The router delegate is responsible for marshaling onto the framework thread before
// touching game memory. Temporary debugging tool — started only via /mhater debug.
public sealed class DebugServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Func<string, Dictionary<string, string>, Task<object?>> router;
    private readonly Action<Exception, string>? logError;
    private TcpListener? listener;
    private CancellationTokenSource? cts;

    public DebugServer(int port, Func<string, Dictionary<string, string>, Task<object?>> router, Action<Exception, string>? logError = null)
    {
        this.Port = port;
        this.router = router;
        this.logError = logError;
    }

    public int Port { get; }

    public bool IsRunning => this.cts is not null;

    public void Start()
    {
        if (this.IsRunning)
            return;

        this.listener = new TcpListener(IPAddress.Loopback, this.Port);
        this.listener.Start();
        this.cts = new CancellationTokenSource();
        _ = this.AcceptLoop(this.listener, this.cts.Token);
    }

    public void Stop()
    {
        this.cts?.Cancel();
        this.cts?.Dispose();
        this.cts = null;
        this.listener?.Stop();
        this.listener = null;
    }

    public void Dispose() => this.Stop();

    private async Task AcceptLoop(TcpListener server, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    this.logError?.Invoke(ex, "[Debug] Accept failed.");
                return;
            }

            _ = this.HandleClient(client, ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();

            var requestLine = await ReadRequestLine(stream, ct);
            if (requestLine is null)
                return;

            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            var (path, query) = ParseTarget(target);

            object? result;
            int status = 200;
            try
            {
                result = await this.router(path, query);
                if (result is null)
                {
                    status = 404;
                    result = new Dictionary<string, object?> { ["error"] = $"unknown endpoint '{path}'" };
                }
            }
            catch (Exception ex)
            {
                this.logError?.Invoke(ex, $"[Debug] Handler for '{path}' failed.");
                status = 500;
                result = new Dictionary<string, object?> { ["error"] = ex.Message };
            }

            // Line sequences render as plain text (node trees); everything else as JSON.
            string body;
            string contentType;
            if (result is string text)
            {
                body = text;
                contentType = "text/plain; charset=utf-8";
            }
            else if (result is IEnumerable<string> lines)
            {
                body = string.Join("\n", lines);
                contentType = "text/plain; charset=utf-8";
            }
            else
            {
                body = JsonSerializer.Serialize(result, JsonOptions);
                contentType = "application/json; charset=utf-8";
            }

            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Error")}\r\n" +
                         $"Content-Type: {contentType}\r\n" +
                         $"Content-Length: {bodyBytes.Length}\r\n" +
                         "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bodyBytes, ct);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                this.logError?.Invoke(ex, "[Debug] Client handling failed.");
        }
    }

    private static async Task<string?> ReadRequestLine(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, ct);
        if (read <= 0)
            return null;

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var lineEnd = text.IndexOf('\r');
        return lineEnd < 0 ? text : text[..lineEnd];
    }

    private static (string Path, Dictionary<string, string> Query) ParseTarget(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var qIndex = target.IndexOf('?');
        var path = qIndex < 0 ? target : target[..qIndex];
        if (qIndex >= 0)
        {
            foreach (var pair in target[(qIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0)
                    query[Uri.UnescapeDataString(pair)] = string.Empty;
                else
                    query[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return (path.TrimEnd('/').Length == 0 ? "/" : path.TrimEnd('/'), query);
    }
}
