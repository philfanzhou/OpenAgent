using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;
namespace OpenAgent.Runtime.Acp.Tests.Protocol;
public sealed class SchemaBaselineTests
{
    [Fact]
    public void OfficialSchema_IsPinnedAndAllSessionUpdatesHaveTypedBranches()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts.json"));
        using JsonDocument schema = JsonDocument.Parse(bytes);
        Assert.Equal(AcpMethods.SourceCommit, schema.RootElement.GetProperty("sourceCommit").GetString());
        Assert.Equal(AcpMethods.All.Select(x => x.Name).Order(), schema.RootElement.GetProperty("methods").EnumerateArray().Select(x => x.GetString()).Order());
        string[] expected = schema.RootElement.GetProperty("updates").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string[] actual = typeof(SessionUpdate).GetCustomAttributes(typeof(JsonDerivedTypeAttribute), false).Cast<JsonDerivedTypeAttribute>().Select(x => (string)x.TypeDiscriminator!).Order().ToArray();
        Assert.Equal(expected, actual);
    }
    [Theory]
    [InlineData("{\"sessionId\":null}")]
    [InlineData("{}")]
    public void RequiredSessionId_MissingOrNull_IsRejected(string json) => Assert.Throws<JsonException>(() => AcpJson.Read<SessionRequest>(AcpJson.Parse(json)));
}
