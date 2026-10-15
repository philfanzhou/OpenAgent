using Microsoft.Extensions.AI;
using OpenAgent.Application.Mapping;
using Xunit;

namespace OpenAgent.Application.Tests.Runtime;

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
