using OpenAgent.Runtime.Acp.Transport;
using Xunit;

namespace OpenAgent.Runtime.Acp.Tests.Architecture;

public sealed class SdkDependencyTests
{
    [Fact]
    public void SdkAssembly_DoesNotReferencePlatformOrConsole()
    {
        string[] references = typeof(IAcpTransport).Assembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name!).ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("OpenAgent.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }
}
