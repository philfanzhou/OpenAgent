using System.Text.Json;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Protocol;

public sealed class RpcIdTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("\"rpc-42\"")]
    [InlineData("\"9007199254740993\"")]
    [InlineData("9007199254740993")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775807")]
    public void Deserialize_ValidId_PreservesTypeAndPrecision(string json)
    {
        RpcId id = AcpJson.Deserialize<RpcId>(json);
        Assert.Equal(json, AcpJson.Serialize(id));
        Assert.Equal(id, AcpJson.Deserialize<RpcId>(AcpJson.Serialize(id)));
        Assert.Equal(id.GetHashCode(), AcpJson.Deserialize<RpcId>(json).GetHashCode());
        CancelRequestNotification cancel = new(id);
        Assert.Equal(id, AcpJson.Deserialize<CancelRequestNotification>(AcpJson.Serialize(cancel)).RequestId);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData("9223372036854775808")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Deserialize_InvalidId_RejectsWithoutRounding(string json)
        => Assert.Throws<JsonException>(() => AcpJson.Deserialize<RpcId>(json));

    [Fact]
    public void OptionalId_NullAndAbsent_AreDifferent()
    {
        Optional<RpcId> absent = default;
        Optional<RpcId> explicitNull = RpcId.Null;
        Assert.False(absent.IsDefined);
        Assert.True(explicitNull.HasValue);
        Assert.True(explicitNull.Value.IsNull);
        Assert.NotEqual(new RpcId(1), new RpcId("1"));
        Assert.Throws<JsonException>(() => AcpJson.Deserialize<CancelRequestNotification>("{}"));
        Assert.True(AcpJson.Deserialize<CancelRequestNotification>("{\"requestId\":null}").RequestId.IsNull);
    }
}
