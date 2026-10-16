using System.Text;
using System.Text.Json;

namespace OpenAgent.Acp.Demo;

/// <summary>A deliberately small v1 agent: no model, authentication, tools or persistence.</summary>
internal sealed class AcpAgent(JsonLineTransport transport)
{
    private const int MaxSessions = 32;
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _turns = [];
    private bool _initialized;

    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (await transport.ReadAsync(lifetime.Token).ConfigureAwait(false) is byte[] line)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    await DispatchAsync(document.RootElement.Clone(), lifetime.Token).ConfigureAwait(false);
                    _turns.RemoveWhere(task => task.IsCompletedSuccessfully);
                    Task? faulted = _turns.FirstOrDefault(task => task.IsFaulted);
                    if (faulted is not null)
                    {
                        await faulted.ConfigureAwait(false);
                    }
                }
                catch (JsonException)
                {
                    await WriteErrorAsync(null, -32700, "Parse error", lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            foreach (AgentSession session in _sessions.Values)
            {
                lock (session)
                {
                    session.ActiveTurn?.Cancel();
                }
            }

            try
            {
                await Task.WhenAll(_turns).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // EOF cancels work; no final response can be promised after disconnection.
            }
        }
    }

    private async Task DispatchAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != "2.0" || !message.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            await WriteErrorAsync(null, -32600, "Invalid Request", cancellationToken).ConfigureAwait(false);
            return;
        }

        string method = methodElement.GetString()!;
        bool hasId = message.TryGetProperty("id", out JsonElement id);
        if (hasId && id.ValueKind != JsonValueKind.String && id.ValueKind != JsonValueKind.Null &&
            (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out _)))
        {
            await WriteErrorAsync(null, -32600, "Invalid request ID", cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonElement parameters = message.TryGetProperty("params", out JsonElement value) ? value : default;
        if (!hasId)
        {
            // Notifications never receive a response, including malformed or unknown ones.
            if (method == "session/cancel" && parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("sessionId", out JsonElement sessionId) &&
                sessionId.ValueKind == JsonValueKind.String &&
                _sessions.TryGetValue(sessionId.GetString()!, out AgentSession? session))
            {
                lock (session)
                {
                    session.ActiveTurn?.Cancel();
                }
            }

            return;
        }

        try
        {
            if (method is not ("initialize" or "session/new" or "session/prompt"))
            {
                throw new RpcException(-32601, "Method not found");
            }

            RequireObject(parameters);
            if (method == "initialize")
            {
                if (_initialized)
                {
                    throw new RpcException(-32600, "Connection is already initialized.");
                }

                if (!parameters.TryGetProperty("protocolVersion", out JsonElement requestedVersion) ||
                    requestedVersion.ValueKind != JsonValueKind.Number ||
                    !requestedVersion.TryGetInt32(out int protocolVersion) || protocolVersion < 0 || protocolVersion > ushort.MaxValue)
                {
                    throw new RpcException(-32602, "protocolVersion must be an integer between 0 and 65535.");
                }

                // Per ACP negotiation, return our latest version even if the peer requested another one.
                _initialized = true;
                await WriteResultAsync(id, new
                {
                    protocolVersion = 1,
                    agentCapabilities = new { },
                    agentInfo = new { name = "dotnet-echo-demo", title = "C# ACP Echo Demo", version = "0.1.0" },
                    authMethods = Array.Empty<object>()
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!_initialized)
            {
                throw new RpcException(-32600, "Initialize with protocol version 1 before creating or using sessions.");
            }

            if (method == "session/new")
            {
                string cwd = ReadString(parameters, "cwd");
                if (!Path.IsPathFullyQualified(cwd))
                {
                    throw new RpcException(-32602, "cwd must be an absolute path.");
                }

                if (!parameters.TryGetProperty("mcpServers", out JsonElement servers) || servers.ValueKind != JsonValueKind.Array)
                {
                    throw new RpcException(-32602, "mcpServers must be an array.");
                }

                if (servers.GetArrayLength() != 0)
                {
                    throw new RpcException(-32602, "This demo only accepts an empty mcpServers array.");
                }

                if (_sessions.Count >= MaxSessions)
                {
                    throw new RpcException(10002, "Demo session limit reached.");
                }

                string sessionId = Guid.NewGuid().ToString("N");
                _sessions.Add(sessionId, new AgentSession());
                await WriteResultAsync(id, new { sessionId }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string activeSessionId = ReadString(parameters, "sessionId");
            if (!_sessions.TryGetValue(activeSessionId, out AgentSession? activeSession))
            {
                throw new RpcException(-32002, "Session not found.");
            }

            string text = ReadPrompt(parameters);
            CancellationTokenSource turn;
            lock (activeSession)
            {
                if (activeSession.ActiveTurn is not null)
                {
                    throw new RpcException(10001, "A prompt is already running for this session.");
                }

                turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                activeSession.ActiveTurn = turn;
            }

            // Keep reading stdin while this turn streams, so session/cancel can arrive.
            _turns.Add(PromptAsync(id, activeSessionId, activeSession, text, turn, cancellationToken));
        }
        catch (RpcException exception)
        {
            await WriteErrorAsync(id, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PromptAsync(JsonElement id, string sessionId, AgentSession session, string text,
        CancellationTokenSource turn, CancellationToken cancellationToken)
    {
        string stopReason = "end_turn";
        try
        {
            try
            {
                foreach (Rune rune in ("Echo: " + text).EnumerateRunes())
                {
                    turn.Token.ThrowIfCancellationRequested();
                    await transport.WriteAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "session/update",
                        @params = new
                        {
                            sessionId,
                            update = new
                            {
                                sessionUpdate = "agent_message_chunk",
                                content = new { type = "text", text = rune.ToString() }
                            }
                        }
                    }, turn.Token).ConfigureAwait(false);
                    await Task.Delay(30, turn.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (turn.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                stopReason = "cancelled";
            }

            await WriteResultAsync(id, new { stopReason }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (session)
            {
                session.ActiveTurn = null;
                turn.Dispose();
            }
        }
    }

    private static string ReadPrompt(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("prompt", out JsonElement prompt) || prompt.ValueKind != JsonValueKind.Array)
        {
            throw new RpcException(-32602, "prompt must be an array of content blocks.");
        }

        List<string> blocks = [];
        foreach (JsonElement block in prompt.EnumerateArray())
        {
            RequireObject(block);
            string type = ReadString(block, "type");
            blocks.Add(type switch
            {
                "text" => ReadString(block, "text", allowEmpty: true),
                "resource_link" => "[resource: " + ReadString(block, "uri") + "]",
                _ => throw new RpcException(-32602, "Only text and resource_link prompt blocks are supported.")
            });
        }

        return string.Join("\n", blocks);
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new RpcException(-32602, "Parameters must be an object.");
        }
    }

    private static string ReadString(JsonElement parameters, string name, bool allowEmpty = false)
    {
        if (!parameters.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String ||
            (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString())))
        {
            throw new RpcException(-32602, $"{name} must be a string.");
        }

        return value.GetString()!;
    }

    private Task WriteResultAsync<T>(JsonElement id, T result, CancellationToken cancellationToken) =>
        transport.WriteAsync(new { jsonrpc = "2.0", id, result }, cancellationToken);

    private Task WriteErrorAsync(JsonElement? id, int code, string message, CancellationToken cancellationToken) =>
        transport.WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } }, cancellationToken);
}
