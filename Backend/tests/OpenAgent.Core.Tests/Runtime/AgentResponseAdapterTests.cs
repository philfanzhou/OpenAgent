using Microsoft.Extensions.AI;
using OpenAgent.Core.Runtime.Agent;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class AgentResponseAdapterTests
{
    [Fact]
    public void ConvertUsage_IncompleteProviderCounts_ReturnsUnavailable()
    {
        UsageDetails usage = new()
        {
            InputTokenCount = 21,
            OutputTokenCount = 8
        };

        var result = AgentResponseAdapter.ConvertUsage(usage);

        Assert.Null(result);
    }
}
