using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenAgent.Runtime.Acp.Protocol.Serialization;

/// <summary>
/// Blocks direct serialization of the abstract <see cref="AcpModel"/> declaration type, whose
/// wire shape lives in concrete protocol types. Reflection serialization would otherwise emit
/// an empty object where <see cref="AcpJson"/> correctly refuses.
/// </summary>
internal sealed class AcpModelGuardConverter : JsonConverter<AcpModel>
{
    public override bool HandleNull => true;
    public override AcpModel? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Deserialize a concrete protocol type through AcpJson, not the abstract AcpModel.");
    public override void Write(Utf8JsonWriter writer, AcpModel value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Serialize a concrete protocol type through AcpJson, not the abstract AcpModel.");
}
