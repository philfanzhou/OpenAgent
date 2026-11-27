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
    private readonly TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reader;
    private bool _hasSummary;

    internal sealed class Endpoint
    {
        private readonly int _port;
        private readonly string _password;

        internal Endpoint(int port, string password)
        {
            _port = port;
            _password = password;
        }

        internal HttpClient CreateClient()
        {
            HttpClient http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{_port}/")
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + _password)));
            return http;
        }
    }

    internal static Endpoint Configure(AcpProcessOptions original, out AcpProcessOptions configured)
    {
        // The child binds its own socket. If another process takes the port in between,
        // process startup retries with the original ACP arguments.
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port;
        try { port = ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }

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

    private OpenCodeCompactionEvents(HttpClient http, string sessionId, string directory)
    {
        _http = http;
        _sessionId = sessionId;
        _directory = "directory=" + Uri.EscapeDataString(directory);
        _reader = ReadEventsAsync();
    }

    internal static async Task<OpenCodeCompactionEvents> StartAsync(
        HttpClient http, string sessionId, string directory, CancellationToken ct)
    {
        OpenCodeCompactionEvents events = new(http, sessionId, directory);
        try
        {
            if (!await events._connected.Task.WaitAsync(EventTimeout, ct).ConfigureAwait(false))
                throw new IOException("OpenCode event stream disconnected before subscribing.");
            return events;
        }
        catch
        {
            await events.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<IReadOnlyList<ContextSummary>> ReadAuditsAsync(HashSet<string> reported, bool auditPending, CancellationToken ct)
    {
        // The native idle notification follows the compaction events on the same stream.
        // ACP completion and HTTP events arrive on separate transports.
        if (!await _idle.Task.WaitAsync(EventTimeout, ct).ConfigureAwait(false))
            throw new IOException("OpenCode event stream disconnected before the session became idle.");
        if (!_hasSummary && !auditPending) return [];

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

    private async Task ReadEventsAsync()
    {
        try
        {
            using Stream stream = await _http.GetStreamAsync("event?" + _directory, _stop.Token).ConfigureAwait(false);
            using StreamReader reader = new(stream);
            bool busy = false;
            while (await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                using JsonDocument json = JsonDocument.Parse(line[5..]);
                JsonElement root = json.RootElement;
                string? type = root.GetProperty("type").GetString();
                if (type == "server.connected")
                {
                    _connected.TrySetResult(true);
                    continue;
                }
                if (!root.TryGetProperty("properties", out JsonElement properties)
                    || !properties.TryGetProperty("sessionID", out JsonElement session)
                    || session.GetString() != _sessionId) continue;

                // A summary message can exist even when compaction fails or does not finish.
                if (type == "session.compacted" || (type == "message.updated"
                    && properties.TryGetProperty("info", out JsonElement info)
                    && OpenCodeCompactionExport.IsSummary(info)))
                {
                    _hasSummary = true;
                }
                if (type == "session.status")
                {
                    string? status = properties.GetProperty("status").GetProperty("type").GetString();
                    if (status == "busy") busy = true;
                    if (status == "idle" && busy)
                    {
                        _idle.TrySetResult(true);
                        return;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or HttpRequestException or JsonException
            or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            // The caller falls back to the export command when the stream is incomplete.
        }
        finally
        {
            _connected.TrySetResult(false);
            _idle.TrySetResult(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _reader.ConfigureAwait(false); }
        finally
        {
            _http.Dispose();
            _stop.Dispose();
        }
    }
}
