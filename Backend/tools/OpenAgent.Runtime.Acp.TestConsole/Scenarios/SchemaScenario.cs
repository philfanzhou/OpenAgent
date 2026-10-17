using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.Acp.TestConsole.Scenarios;

internal static class SchemaScenario
{
    private static readonly string[] EnvelopeSchemas =
        ["ClientRequest", "AgentRequest", "ClientResponse", "AgentResponse", "ClientNotification", "AgentNotification"];

    internal static async Task<JsonObject> RunAsync(string path)
    {
        string fixtures = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(fixtures);
        JsonElement root = document.RootElement;
        if (root.GetProperty("protocolVersion").GetInt32() != AcpProtocol.Version || root.GetProperty("sourceCommit").GetString() != AcpProtocol.SourceCommit)
            throw new InvalidDataException("Fixture protocol baseline mismatch.");
        HashSet<string> definitions = new(StringComparer.Ordinal);
        HashSet<string> updates = new(StringComparer.Ordinal);
        Dictionary<string, int> envelopes = EnvelopeSchemas.ToDictionary(schema => schema, _ => 0, StringComparer.Ordinal);
        int cases = 0;
        foreach (JsonElement row in root.GetProperty("cases").EnumerateArray())
        {
            string schema = row.GetProperty("schema").GetString()!;
            AcpSchemaDescriptor descriptor = AcpProtocol.Schemas[schema];
            JsonElement input = row.GetProperty("value");
            object model = descriptor.Deserialize(input.GetRawText());
            JsonElement serialized = JsonSerializer.SerializeToElement(model, descriptor.JsonTypeInfo);
            descriptor.ValidateForSend(serialized);
            if (!JsonNode.DeepEquals(JsonNode.Parse(input.GetRawText()), JsonNode.Parse(serialized.GetRawText())))
                throw new InvalidDataException("A fixed fixture changed during round trip.");
            definitions.Add(schema);
            if (schema == "SessionUpdate") updates.Add(serialized.GetProperty("sessionUpdate").GetString()!);
            if (row.TryGetProperty("id", out JsonElement id) && id.GetString()?.StartsWith("envelope-", StringComparison.Ordinal) == true)
                envelopes[schema]++;
            cases++;
        }
        if (!definitions.SetEquals(AcpProtocol.Schemas.Keys) || !updates.SetEquals(AcpProtocol.SessionUpdates))
            throw new InvalidDataException("Incomplete fixed fixture coverage.");
        JsonObject envelopeCoverage = EnvelopeCoverage(envelopes);
        int rootTypeRejections = 0;
        foreach (AcpMethodDescriptor method in AcpProtocol.Methods)
        {
            foreach (string? name in new[] { method.RequestSchema, method.ResponseSchema })
            {
                if (name is null) continue;
                ExpectRejected(() => AcpProtocol.Schemas[name].Deserialize("[]"));
                rootTypeRejections++;
            }
        }
        int envelopeRejections = 0;
        ExpectRejected(() => AcpJson.Deserialize<ClientRequest>("""{"id":1,"method":"fs/read_text_file","params":{"path":"/tmp/a"}}"""));
        envelopeRejections++; // a known method of the other direction is not a client request
        ExpectRejected(() => AcpJson.Deserialize<ClientRequest>("""{"id":1,"method":"session/prompt","params":{"sessionId":"s1"}}"""));
        envelopeRejections++; // params malformed for the envelope's own method
        ExpectRejected(() => AcpJson.Deserialize<AgentResponse>("""{"id":1,"result":{"stopReason":"end_turn"},"error":{"code":-32603,"message":"m"}}"""));
        envelopeRejections++; // result and error are mutually exclusive
        int targetedRejections = 0;
        ExpectRejected(() => AcpJson.Deserialize<PromptRequest>("{\"sessionId\":\"s\",\"prompt\":[{\"type\":\"text\"}]}"));
        targetedRejections++;
        ExpectRejected(() => new ToolCallUpdate("tool-1", kind: "unknown"));
        targetedRejections++;
        InitializeRequest defaults = AcpJson.Deserialize<InitializeRequest>("{\"protocolVersion\":1,\"clientCapabilities\":false}");
        if (defaults.ClientCapabilitiesOrDefault.TerminalOrDefault) throw new InvalidDataException("Invalid default capability.");
        ClientCapabilities capabilities = AcpJson.Deserialize<ClientCapabilities>("{\"elicitation\":{\"form\":{},\"url\":null}}");
        if (!capabilities.Elicitation.Value.Form.HasValue || !capabilities.Elicitation.Value.Url.IsNull)
            throw new InvalidDataException("Empty capability object lost.");
        if (AcpJson.Deserialize<RpcId>("9007199254740993").NumberValue != 9007199254740993L)
            throw new InvalidDataException("Request ID precision lost.");
        return new JsonObject
        {
            ["definitionCount"] = definitions.Count, ["methodCount"] = AcpProtocol.Methods.Count,
            ["updateCount"] = updates.Count, ["fixtureCount"] = cases,
            ["envelopes"] = envelopeCoverage,
            ["rejectedInputs"] = new JsonObject
            {
                ["rootTypeRejections"] = rootTypeRejections,
                ["envelopeRejections"] = envelopeRejections,
                ["targetedRejections"] = targetedRejections
            },
            ["capabilities"] = "Model coverage only; no methods are callable in P1.",
            ["peer"] = new JsonObject { ["kind"] = "fixed-fixture", ["version"] = AcpProtocol.SourceCommit }
        };
    }

    /// <summary>Every direction must reach the console through envelope payloads; the fixture
    /// matrix itself stays pinned in the automated contract tests.</summary>
    private static JsonObject EnvelopeCoverage(Dictionary<string, int> envelopes)
    {
        foreach (AcpMethodDescriptor method in AcpProtocol.Methods)
        {
            if (method.RequestSchema == "CancelRequestNotification") continue;
            bool client = method.Direction == "client_to_agent";
            string requestSchema = method.Kind == "notification"
                ? (client ? "ClientNotification" : "AgentNotification")
                : (client ? "ClientRequest" : "AgentRequest");
            if (envelopes[requestSchema] == 0) throw new InvalidDataException("No envelope fixture for " + method.Name);
            if (method.ResponseSchema is not null && envelopes[client ? "AgentResponse" : "ClientResponse"] == 0)
                throw new InvalidDataException("No response envelope fixture for " + method.Name);
        }
        return new JsonObject(envelopes.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
    }

    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (JsonException) { return; }
        throw new InvalidDataException("Invalid protocol input was accepted.");
    }
}
