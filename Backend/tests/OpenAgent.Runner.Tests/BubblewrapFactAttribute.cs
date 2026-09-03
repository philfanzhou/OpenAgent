using Xunit;

namespace OpenAgent.Runner.Tests;

internal sealed class BubblewrapFactAttribute : FactAttribute
{
    public BubblewrapFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_CODEACT_BWRAP_TESTS") != "1")
        {
            Skip = "Set RUN_CODEACT_BWRAP_TESTS=1 on a configured Linux host to run real Bubblewrap tests.";
        }
        else if (!OperatingSystem.IsLinux())
        {
            Skip = "Bubblewrap execution is supported only on Linux.";
        }
    }
}
