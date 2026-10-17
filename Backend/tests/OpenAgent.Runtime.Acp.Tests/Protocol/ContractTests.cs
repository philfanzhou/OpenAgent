using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Protocol;

public sealed class ContractTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts.json");

    public static IEnumerable<object[]> Cases()
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Fixtures));
        return fixtures.RootElement.GetProperty("cases").EnumerateArray()
            .Select(row => new object[] { row.GetProperty("id").GetString()!, row.GetProperty("schema").GetString()!, row.GetProperty("value").GetRawText() }).ToArray();
    }

    internal static (string Id, string Schema, string Json, string[] Required)[] CaseRows()
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Fixtures));
        return fixtures.RootElement.GetProperty("cases").EnumerateArray()
            .Select(row => (
                Id: row.GetProperty("id").GetString()!,
                Schema: row.GetProperty("schema").GetString()!,
                Json: row.GetProperty("value").GetRawText(),
                Required: row.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToArray()))
            .ToArray();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Deserialize_PinnedContract_RoundTripsStrictly(string id, string schema, string json)
    {
        AcpSchemaDescriptor descriptor = AcpProtocol.Schemas[schema];
        object model = descriptor.Deserialize(json);
        VerifyPublicValues(model);
        JsonElement output = JsonSerializer.SerializeToElement(model, descriptor.JsonTypeInfo);
        descriptor.ValidateForSend(output);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(output.GetRawText())), id);
    }

    private static void VerifyPublicValues(object? value)
    {
        if (value is null) return;
        Type type = value.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>))
        {
            if ((bool)type.GetProperty("HasValue")!.GetValue(value)!) VerifyPublicValues(type.GetProperty("Value")!.GetValue(value));
        }
        else if (value is AcpModel)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                VerifyPublicValues(property.GetValue(value));
            MethodInfo[] projections = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.Name.StartsWith("As", StringComparison.Ordinal) && method.GetParameters().Length == 0).ToArray();
            bool projected = projections.Length == 0;
            foreach (MethodInfo method in projections)
            {
                object? branch;
                try { branch = method.Invoke(value, null); }
                catch (TargetInvocationException error) when (error.InnerException is JsonException) { continue; }
                VerifyPublicValues(branch);
                projected = true;
            }
            Assert.True(projected, "No typed projection for " + type.Name);
        }
        else if (value is IDictionary dictionary)
        {
            foreach (object? item in dictionary.Values) VerifyPublicValues(item);
        }
        else if (value is IEnumerable sequence && value is not string)
        {
            foreach (object? item in sequence) VerifyPublicValues(item);
        }
    }

    [Fact]
    public void Catalog_FixedInputs_CoversEveryStableMethodAndUpdate()
    {
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Fixtures));
        Assert.Equal(1, AcpProtocol.Version);
        Assert.Equal(AcpProtocol.SourceCommit, fixtures.RootElement.GetProperty("sourceCommit").GetString());
        Assert.Equal(178, AcpProtocol.Schemas.Count);
        Assert.Equal(25, AcpProtocol.Methods.Count);
        Assert.Equal(14, AcpProtocol.SessionUpdates.Count);
        Assert.Equal(21, AcpProtocol.Methods.Count(method => method.Kind == "request"));
        Assert.Equal(4, AcpProtocol.Methods.Count(method => method.Kind == "notification"));
        (string Id, string Schema, string Json, string[] Required)[] rows = CaseRows();
        HashSet<string> covered = rows.Select(row => row.Schema).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(AcpProtocol.Schemas.Keys.Order(), covered.Order());
        foreach (AcpMethodDescriptor method in AcpProtocol.Methods)
        {
            Assert.Contains(method.RequestSchema, covered);
            if (method.ResponseSchema is not null) Assert.Contains(method.ResponseSchema, covered);
        }
        string[] updates = fixtures.RootElement.GetProperty("cases").EnumerateArray()
            .Where(row => row.GetProperty("id").GetString()!.StartsWith("update-", StringComparison.Ordinal))
            .Select(row => row.GetProperty("value").GetProperty("sessionUpdate").GetString()!).ToArray();
        Assert.Equal(AcpProtocol.SessionUpdates.Order(), updates.Order());
        // Every object case carries its locked-schema required set, and the sample satisfies it.
        foreach ((string id, string schema, string json, string[] required) in rows)
        {
            if (JsonNode.Parse(json) is not JsonObject value) continue;
            foreach (string key in required) Assert.True(value.ContainsKey(key), $"{id} misses required {key}.");
        }
    }

    public static IEnumerable<object[]> MethodSchemas() => AcpProtocol.Methods
        .SelectMany(method => new[] { method.RequestSchema, method.ResponseSchema }).Where(name => name is not null)
        .Select(name => new object[] { name! });

    [Theory]
    [MemberData(nameof(MethodSchemas))]
    public void Deserialize_EveryMethod_RejectsWrongRootType(string schema)
    {
        Assert.Throws<JsonException>(() => AcpProtocol.Schemas[schema].Deserialize("[]"));
        Assert.Throws<JsonException>(() => AcpProtocol.Schemas[schema].Deserialize("null"));
    }

    [Theory]
    [MemberData(nameof(MethodSchemas))]
    public void Deserialize_RequiredMethodProperties_CannotBeRecovered(string schema)
    {
        // The required set is pinned fixture data from the locked schema (see fixtures/build.py),
        // so the assertions here never derive "required" from the sample's own keys.
        (string Id, string Schema, string Json, string[] Required) row = CaseRows().First(row => row.Schema == schema);
        if (row.Required.Length == 0)
        {
            // Explicit no-op marker: the locked schema declares no required property for this
            // method shape, so there is nothing to strip. Branch-level discrimination for the
            // union shapes among them is covered by ModelTests.Union_KnownMalformedPayload.
            Assert.Empty(row.Required);
            return;
        }
        JsonObject input = JsonNode.Parse(row.Json)!.AsObject();
        foreach (string key in row.Required)
        {
            JsonObject missing = (JsonObject)input.DeepClone(); missing.Remove(key);
            Assert.Throws<JsonException>(() => AcpProtocol.Schemas[schema].Deserialize(missing.ToJsonString()));
        }
    }
}
