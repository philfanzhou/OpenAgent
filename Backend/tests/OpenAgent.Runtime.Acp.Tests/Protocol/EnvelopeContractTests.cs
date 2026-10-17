using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Protocol;

/// <summary>
/// Regression gates for the JSON-RPC envelope models: a payload must never be silently
/// dropped by union normalization, the envelope method discriminates request and
/// notification params, and response payloads stay verbatim until a typed projection
/// validates the exact variant.
/// </summary>
public sealed class EnvelopeContractTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts.json");

    private static (string Id, string Schema, string Json, string[] Required)[] EnvelopeRows() =>
        ContractTests.CaseRows().Where(row => row.Id.StartsWith("envelope-", StringComparison.Ordinal)).ToArray();

    public static IEnumerable<object[]> EnvelopeCases() => EnvelopeRows()
        .Select(row => new object[] { row.Id, row.Schema, row.Json });

    [Theory]
    [MemberData(nameof(EnvelopeCases))]
    public void Envelope_ReadsAndWritesWithoutLoss(string id, string schema, string json)
    {
        string output = schema switch
        {
            "ClientRequest" => AcpJson.Serialize(AcpJson.Deserialize<ClientRequest>(json)),
            "AgentRequest" => AcpJson.Serialize(AcpJson.Deserialize<AgentRequest>(json)),
            "ClientResponse" => AcpJson.Serialize(AcpJson.Deserialize<ClientResponse>(json)),
            "AgentResponse" => AcpJson.Serialize(AcpJson.Deserialize<AgentResponse>(json)),
            "ClientNotification" => AcpJson.Serialize(AcpJson.Deserialize<ClientNotification>(json)),
            "AgentNotification" => AcpJson.Serialize(AcpJson.Deserialize<AgentNotification>(json)),
            _ => throw new InvalidOperationException("Unknown envelope schema " + schema)
        };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(output)), id);
    }

    [Fact]
    public void EveryMethodPayload_HasDirectionEnvelope()
    {
        (string Id, string Schema, string Json, string[] Required)[] rows = EnvelopeRows();
        HashSet<string> ids = rows.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        int envelopes = 0;
        foreach (AcpMethodDescriptor method in AcpProtocol.Methods)
        {
            // The protocol-side $/cancel_request notification has no envelope union upstream;
            // it stays covered at definition level, so the matrix is 45 envelope fixtures.
            if (method.RequestSchema == "CancelRequestNotification")
            {
                Assert.DoesNotContain("envelope-$/cancel_request-notification", ids);
                continue;
            }
            bool client = method.Direction == "client_to_agent";
            bool notification = method.Kind == "notification";
            string requestKind = notification ? "notification" : "request";
            envelopes++;
            Assert.Contains($"envelope-{method.Name}-{requestKind}", ids);
            Assert.Equal(notification
                ? (client ? "ClientNotification" : "AgentNotification")
                : (client ? "ClientRequest" : "AgentRequest"),
                rows.Single(row => row.Id == $"envelope-{method.Name}-{requestKind}").Schema);
            if (method.ResponseSchema is not null)
            {
                envelopes++;
                Assert.Contains($"envelope-{method.Name}-response", ids);
                Assert.Equal(client ? "AgentResponse" : "ClientResponse",
                    rows.Single(row => row.Id == $"envelope-{method.Name}-response").Schema);
            }
        }
        Assert.Equal(45, envelopes);
    }

    [Fact]
    public void SessionPrompt_Params_ProjectsPromptRequestAndRejectsLogout()
    {
        string json = EnvelopeRows().Single(row => row.Id == "envelope-session/prompt-request").Json;
        ClientRequest request = AcpJson.Deserialize<ClientRequest>(json);
        Assert.Equal("sess-1", request.Params.Value.AsPromptRequest().SessionId);
        Assert.Throws<JsonException>(() => request.Params.Value.AsLogoutRequest());
        // The same discrimination holds for the synthesized union wrapper read alone.
        ClientRequestParams wrapper = AcpJson.Deserialize<ClientRequestParams>(request.Params.Value.Json.GetRawText());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(request.Params.Value.Json.GetRawText()), JsonNode.Parse(wrapper.Json.GetRawText())));
        Assert.Throws<JsonException>(() => wrapper.AsLogoutRequest());
    }

    [Fact]
    public void Envelope_ParamsMalformedForItsMethod_IsRejected()
    {
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<ClientRequest>(
            """{"id":1,"method":"session/prompt","params":{"sessionId":"s1"}}"""));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<ClientRequest>(
            """{"id":1,"method":"session/new","params":{"sessionId":"s1"}}"""));
    }

    [Fact]
    public void Envelope_WrongDirectionMethod_IsRejected()
    {
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<ClientRequest>(
            """{"id":1,"method":"fs/read_text_file","params":{"path":"/tmp/a"}}"""));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<AgentRequest>(
            """{"id":1,"method":"session/prompt","params":{"sessionId":"s1","prompt":[]}}"""));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<ClientNotification>(
            """{"method":"session/update","params":{"sessionId":"s1","update":{"sessionUpdate":"user_message_chunk","content":{"type":"text","text":"x"}}}}"""));
    }

    [Fact]
    public void Envelope_UnknownMethod_IsCarriedByExtVariantAndPreserved()
    {
        const string json = """{"id":1,"method":"ext/custom_probe","params":{"anything":{"deep":true}}}""";
        ClientRequest request = AcpJson.Deserialize<ClientRequest>(json);
        Assert.True(request.Params.Value.AsExtMethodRequest().Json.TryGetProperty("anything", out _));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(AcpJson.Serialize(request))));
    }

    [Fact]
    public void Response_ResultAndError_AreMutuallyExclusive()
    {
        const string json = """{"id":1,"result":{"stopReason":"end_turn"},"error":{"code":-32603,"message":"m"}}""";
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<AgentResponse>(json));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<ClientResponse>(
            """{"id":1,"result":{},"error":{"code":-32603,"message":"m"}}"""));
    }

    [Fact]
    public void Response_Result_IsPreservedAndValidatedByTypedProjection()
    {
        const string json = """{"id":1,"result":{"sessionId":"s1"}}""";
        AgentResponse response = AcpJson.Deserialize<AgentResponse>(json);
        Assert.Equal("s1", response.AsResult().Result.AsNewSessionResponse().SessionId);
        Assert.Throws<JsonException>(() => response.AsResult().Result.AsPromptResponse());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(AcpJson.Serialize(response))));
        // A non-object payload matches no response schema and is rejected explicitly.
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<AgentResponse>("""{"id":1,"result":"ok"}"""));
    }

    [Fact]
    public void UnionWrapper_StandaloneReads_PreservePayload()
    {
        // Without a request method the synthesized unions are verbatim views; the typed
        // As* projections carry the schema validation for the exact variant.
        AgentResponseResultResult wrapper = AcpJson.Deserialize<AgentResponseResultResult>("""{"sessionId":"s1"}""");
        Assert.Equal("s1", wrapper.AsNewSessionResponse().SessionId);
        Assert.Throws<JsonException>(() => wrapper.AsPromptResponse());
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<AgentResponseResultResult>("[]"));
    }

    [Theory]
    [InlineData("""{"type":"bogus","id":"x","name":"n"}""")]
    [InlineData("""{"type":"terminal","id":"x"}""")]
    public void AuthMethod_UnknownOrMismatchedTag_IsRejected(string json)
        => Assert.Throws<JsonException>(() => AcpJson.Deserialize<AuthMethod>(json));

    [Theory]
    [InlineData("""{"type":"agent","id":"x","name":"n"}""", true)]
    [InlineData("""{"id":"x","name":"n"}""", false)]
    public void AuthMethod_ExplicitAndMissingAgentTag_AreTheAgentVariant(string json, bool explicitTag)
    {
        AuthMethod method = AcpJson.Deserialize<AuthMethod>(json);
        Assert.Equal("x", method.AsAgent().Id);
        Assert.Equal(explicitTag, method.Json.TryGetProperty("type", out _));
    }
}
