using OpenAgent.Runtime.Tests;

namespace OpenAgent.Engine.Tests.TestDoubles;

internal sealed class CapacityFactAttribute : OpenCodeFactAttribute
{
    public CapacityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPENAGENT_RUN_CAPACITY") != "1" || OperatingSystem.IsWindows())
            Skip = "Set OPENAGENT_RUN_CAPACITY=1 on macOS/Linux to run the resource-bounded capacity baseline.";
    }
}
