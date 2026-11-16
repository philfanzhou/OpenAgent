using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Runtime.Acp.Protocol;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class AcpCompactionMapperTests
{
    [Fact]
    public void Map_ChunksReplacementAndClearing_FollowsAcpPatchSemantics()
    {
        AcpEventMapper mapper = new(sessionId: "native-1");
        ContextSummary Read(SessionUpdate update) => Assert.Single(mapper.Map(update)).Compaction!;
        ContextSummary started = Read(new CompactionUpdate("c1", "in_progress"));
        Assert.Equal("InProgress", started.Status);
        Assert.Equal("first", Read(new CompactionSummaryChunk("c1", new TextContent("first"))).Summary);
        Assert.Equal("first second", Read(new CompactionSummaryChunk("c1", new TextContent(" second"))).Summary);
        ContextSummary complete = Read(new CompactionUpdate("c1", "completed")
        {
            Summary = new ContentBlock[] { new TextContent("replacement") }
        });
        Assert.Equal("Succeeded", complete.Status);
        Assert.Equal("replacement", complete.Summary);
        Assert.Equal(started.CompressionId, complete.CompressionId);
        Assert.Equal("native-1", complete.RuntimeSessionId);
        Assert.True(complete.IsRuntimeEvent);
        Assert.Empty(complete.CompactedMessages);
        Assert.Equal("replacement", Read(new CompactionUpdate("c1", "completed")).Summary);
        Assert.Null(Read(new CompactionUpdate("c1", "completed") { Summary = Optional<ContentBlock[]>.Null }).Summary);
        Assert.Equal("after clear", Read(new CompactionSummaryChunk("c1", new TextContent("after clear"))).Summary);
        Assert.Null(Read(new CompactionUpdate("c1", "completed") { Summary = Array.Empty<ContentBlock>() }).Summary);
    }

    [Fact]
    public void Map_SessionScopedIdsAndFailures_DoNotLeakProviderErrors()
    {
        CompactionUpdate failed = new("same-id", "failed") { Error = "api-key=must-not-leak" };
        ContextSummary first = Assert.Single(new AcpEventMapper(sessionId: "one").Map(failed)).Compaction!;
        ContextSummary second = Assert.Single(new AcpEventMapper(sessionId: "two").Map(failed)).Compaction!;
        Assert.NotEqual(first.CompressionId, second.CompressionId);
        Assert.DoesNotContain("must-not-leak", first.Error);
        Assert.Equal("Failed", first.Status);
    }
}
