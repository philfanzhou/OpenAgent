using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class HttpSkillScriptSandbox : ISkillScriptSandbox, IDisposable
{
    private readonly HttpClient _client;
    private readonly SkillScriptSandboxOptions _options;

    public HttpSkillScriptSandbox(IOptions<SkillScriptSandboxOptions> options)
    {
        _options = options.Value;
        Validate(_options);

        HttpMessageHandler handler = string.IsNullOrWhiteSpace(_options.UnixSocketPath)
            ? new SocketsHttpHandler()
            : CreateUnixSocketHandler(_options.UnixSocketPath);
        _client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(
                string.IsNullOrWhiteSpace(_options.Endpoint)
                    ? "http://localhost"
                    : _options.Endpoint,
                UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds + 5)
        };

        Status = new SkillScriptSandboxStatus
        {
            Enabled = true,
            Isolation = string.IsNullOrWhiteSpace(_options.UnixSocketPath)
                ? "remote-http"
                : "container-unix-socket",
            SupportedExtensions = _options.AllowedExtensions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            TimeoutSeconds = _options.TimeoutSeconds,
            MaxScriptBytes = _options.MaxScriptBytes,
            MaxOutputBytes = _options.MaxOutputBytes
        };
    }

    public SkillScriptSandboxStatus Status { get; }

    public async Task<SkillScriptExecutionResult> ExecuteAsync(
        SkillScriptExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request, _options);
        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/v1/execute",
            request,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Skill sandbox rejected the script with HTTP {(int)response.StatusCode}: {detail}");
        }

        return await response.Content.ReadFromJsonAsync<SkillScriptExecutionResult>(
                cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Skill sandbox returned an empty response.");
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static SocketsHttpHandler CreateUnixSocketHandler(string socketPath)
    {
        return new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(
                        new UnixDomainSocketEndPoint(socketPath),
                        cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }

    private static void Validate(SkillScriptSandboxOptions options)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("The HTTP Skill sandbox cannot be created while disabled.");
        }
        if (string.IsNullOrWhiteSpace(options.Endpoint)
            && string.IsNullOrWhiteSpace(options.UnixSocketPath))
        {
            throw new InvalidOperationException(
                "SkillSandbox requires either Endpoint or UnixSocketPath when enabled.");
        }
        if (options.TimeoutSeconds <= 0
            || options.MaxScriptBytes <= 0
            || options.MaxOutputBytes <= 0
            || options.MaxArgumentCount <= 0
            || options.MaxArgumentLength <= 0)
        {
            throw new InvalidOperationException("SkillSandbox limits must be positive.");
        }
        if (options.AllowedExtensions.Count == 0)
        {
            throw new InvalidOperationException("SkillSandbox requires at least one allowed extension.");
        }
    }

    private static void Validate(
        SkillScriptExecutionRequest request,
        SkillScriptSandboxOptions options)
    {
        string extension = Path.GetExtension(request.ScriptName);
        if (!options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Skill script extension '{extension}' is not allowed by the sandbox policy.");
        }
        if (request.Script.Length == 0 || request.Script.Length > options.MaxScriptBytes)
        {
            throw new InvalidOperationException(
                $"Skill script must be between 1 and {options.MaxScriptBytes} bytes.");
        }
        if (request.Arguments.Count > options.MaxArgumentCount
            || request.Arguments.Any(argument => argument.Length > options.MaxArgumentLength))
        {
            throw new InvalidOperationException("Skill script arguments exceed the sandbox policy.");
        }
    }
}
