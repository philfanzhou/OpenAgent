using Microsoft.Extensions.Options;
using OpenAgent.Core.Capabilities.Skill;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities;

public sealed class UnixSocketSkillScriptSandboxTests
{
    [Fact]
    public void Constructor_RequiresAbsoluteSocketPath()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new UnixSocketSkillScriptSandbox(
                Options.Create(new SkillScriptSandboxOptions
                {
                    Enabled = true,
                    UnixSocketPath = "relative.sock"
                })));

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
