using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Runtime.Acp.Processes;

namespace OpenAgent.Runtime.OpenCode;

// OpenCode 1.19.3 exposes compaction on its native HTTP stream alongside ACP.
internal sealed class OpenCodeCompactionEvents : IAsyncDisposable
{
    internal const string SupportedVersion = "1.19.3";
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _http;
    private readonly string _sessionId;
    private readonly string _directory;
    private readonly CancellationTokenSource _stop = new();
    private readonly StreamReader _stream;
    private readonly Task<bool> _hasSummary;

    internal sealed class Endpoint(int port, string password)
    {
        internal HttpClient CreateClient()
        {
            HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/")
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
            return http;
        }
    }

    internal static Endpoint Configure(AcpProcessOptions original, out AcpProcessOptions configured)
    {
        // The child binds its own socket. If another process takes the port in between,
        // process startup retries with the original ACP arguments.
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string password = Guid.NewGuid().ToString("N");
        Dictionary<string, string> environment = new(original.Environment ?? new Dictionary<string, string>())
        {
            ["OPENCODE_SERVER_USERNAME"] = "opencode",
            ["OPENCODE_SERVER_PASSWORD"] = password
        };
        configured = original with
        {
            Arguments = [.. original.Arguments, "--hostname", "127.0.0.1", "--port",
                port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Environment = environment
        };
        return new Endpoint(port, password);
    }

    private OpenCodeCompactionEvents(HttpClient http, StreamReader stream, string sessionId, string directory)
    {
        _http = http;
        _stream = stream;
        _sessionId = sessionId;
        _directory = directory;
        _hasSummary = ReadUntilIdleAsync();
    }

    internal static async Task<OpenCodeCompactionEvents> StartAsync(
        HttpClient http, string sessionId, string directory, CancellationToken ct)
    {
        string workspace = "directory=" + Uri.EscapeDataString(directory);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(EventTimeout);
        StreamReader? stream = null;
        try
        {
            stream = new StreamReader(await http.GetStreamAsync("event?" + workspace, deadline.Token).ConfigureAwait(false));
            using JsonDocument connected = await ReadNotificationAsync(stream, deadline.Token).ConfigureAwait(false);
            if (connected.RootElement.GetProperty("type").GetString() != "server.connected")
                throw new IOException("OpenCode event stream did not confirm the subscription.");
            return new OpenCodeCompactionEvents(http, stream, sessionId, workspace);
        }
        catch
        {
            stream?.Dispose();
            http.Dispose();
            throw;
        }
    }

    internal async Task<IReadOnlyList<ContextSummary>> ReadAuditsAsync(
        HashSet<string> reported, bool auditPending, CancellationToken ct)
    {
        // ACP completion can arrive before idle on the native event stream.
        bool hasSummary = await _hasSummary.WaitAsync(EventTimeout, ct).ConfigureAwait(false);
        if (!hasSummary && !auditPending) return [];

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(EventTimeout);
        using HttpResponseMessage response = await _http.GetAsync(
            $"session/{Uri.EscapeDataString(_sessionId)}/message?{_directory}",
            HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(OpenCodeCompactionExport.MaxExportBytes, deadline.Token)
            .ConfigureAwait(false);
        await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using JsonDocument messages = await JsonDocument.ParseAsync(body, cancellationToken: deadline.Token)
            .ConfigureAwait(false);
        return OpenCodeCompactionExport.ReadMessages(messages.RootElement, _sessionId, reported);
    }

    private async Task<bool> ReadUntilIdleAsync()
    {
        bool hasSummary = false;
        bool busy = false;
        while (true)
        {
            using JsonDocument json = await ReadNotificationAsync(_stream, _stop.Token).ConfigureAwait(false);
            JsonElement root = json.RootElement;
            if (!root.TryGetProperty("properties", out JsonElement properties)
                || !properties.TryGetProperty("sessionID", out JsonElement session)
                || session.GetString() != _sessionId) continue;

            switch (root.GetProperty("type").GetString())
            {
                case "session.compacted":
                // This also picks up failed or incomplete summaries that never emit session.compacted.
                case "message.updated" when properties.TryGetProperty("info", out JsonElement info)
                    && OpenCodeCompactionExport.IsSummary(info):
                    hasSummary = true;
                    break;
                case "session.status":
                    string? status = properties.GetProperty("status").GetProperty("type").GetString();
                    if (status == "idle" && busy) return hasSummary;
                    busy |= status == "busy";
                    break;
            }
        }
    }

    private static async Task<JsonDocument> ReadNotificationAsync(StreamReader stream, CancellationToken ct)
    {
        while (await stream.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) return JsonDocument.Parse(line[5..]);
        }
        throw new IOException("OpenCode event stream closed before the session became idle.");
    }

    internal static bool CanFallback(Exception error, CancellationToken ct) =>
        !ct.IsCancellationRequested && error is IOException or HttpRequestException or JsonException
            or InvalidOperationException or KeyNotFoundException or OperationCanceledException or TimeoutException;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _hasSummary.ConfigureAwait(false); }
        catch (Exception error) when (CanFallback(error, CancellationToken.None))
        {
            // The turn handles stream failures through the export fallback.
        }
        finally
        {
            _stream.Dispose();
            _http.Dispose();
            _stop.Dispose();
        }
    }
}
