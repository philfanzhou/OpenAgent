using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenAgent.Runtime.Acp.Protocol.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol;

public sealed class AcpSchemaDescriptor
{
    internal AcpSchemaDescriptor(string name, Type type) { Name = name; ClrType = type; }
    public string Name { get; }
    public Type ClrType { get; }
    /// <summary>
    /// Raw System.Text.Json metadata for report and host-tooling plumbing. Serializing through
    /// this property applies no schema validation; use <see cref="Deserialize"/> or
    /// <see cref="ValidateForSend"/> to enforce the pinned schema (scalar aliases such as
    /// ToolKind are free-form strings in the locked v1 schema, so no alias constraint exists).
    /// </summary>
    public JsonTypeInfo JsonTypeInfo => AcpJsonContext.Default.GetTypeInfo(ClrType)
        ?? throw new NotSupportedException("The schema type is not registered.");
    /// <summary>Compatible read, including schema opt-in default-on-error and skip-invalid-items.</summary>
    public object Deserialize(string json)
    {
        JsonElement normalized = SchemaValidator.Process(Name, AcpJson.ParseElement(json), compatibility: true);
        return JsonSerializer.Deserialize(normalized, JsonTypeInfo)
            ?? throw new JsonException("A protocol value cannot be null.");
    }
    /// <summary>Validate an outbound value strictly, with no compatibility recovery.</summary>
    public void ValidateForSend(JsonElement value) => SchemaValidator.Process(Name, value, compatibility: false);
}
