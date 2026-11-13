using System.Text.Json;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeCompactionExportTests
{
    [Fact]
    public void ReadSummaries_NativeMetadata_ExcludesNormalRepliesAndDoesNotRepeatCompletedAudits()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"info":{"id":"session"},"messages":[
              {"info":{"id":"user","role":"user"},"parts":[]},
              {"info":{"id":"reply","role":"assistant","time":{"completed":1}},"parts":[{"type":"text","text":"normal reply"}]},
              {"info":{"id":"summary","role":"assistant","summary":true,"time":{"completed":1}},"parts":[{"type":"text","text":"retained"},{"type":"reasoning","text":"hidden"}]}
            ]}
            """);
        HashSet<string> reported = [];
        var summary = Assert.Single(OpenCodeCompactionExport.ReadSummaries(document.RootElement, "session", reported));
        Assert.Equal("retained", summary.Summary);
        Assert.Equal("Succeeded", summary.Status);
        Assert.True(summary.IsRuntimeEvent);
        Assert.Empty(summary.CompactedMessages);
        Assert.Empty(OpenCodeCompactionExport.ReadSummaries(document.RootElement, "session", reported));
        Assert.Throws<InvalidOperationException>(() => OpenCodeCompactionExport.ReadSummaries(document.RootElement, "other-session", []));
    }
}
