using Xunit;

namespace OpenAgent.Runner.Tests;

internal sealed class GVisorFactAttribute : FactAttribute
{
    public GVisorFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_CODEACT_GVISOR_TESTS") != "1")
        {
            Skip = "Set RUN_CODEACT_GVISOR_TESTS=1 on a configured Linux host to run real GVisor tests.";
        }
    }
}
