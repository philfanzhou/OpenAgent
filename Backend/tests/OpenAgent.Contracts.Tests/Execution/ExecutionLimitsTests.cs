using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Contracts.Tests.Execution;

public class ExecutionLimitsTests
{
    [Theory]
    [InlineData("python")]
    [InlineData("javascript")]
    [InlineData("JavaScript")]
    public void Validate_AcceptsSupportedLanguages(string language)
    {
        ExecutionLimits.Validate(new CodeExecutionRequest { Code = "print(42)", Language = language });
    }

    [Theory]
    [InlineData("ruby")]
    [InlineData("")]
    public void Validate_RejectsUnsupportedLanguage(string language)
    {
        Assert.Throws<ArgumentException>(
            () => ExecutionLimits.Validate(new CodeExecutionRequest { Code = "print(42)", Language = language }));
    }

    [Theory]
    [InlineData("main.py")]
    [InlineData("main.mjs")]
    [InlineData("MAIN.MJS")]
    public void Validate_RejectsReservedEntryNames(string name)
    {
        var request = new CodeExecutionRequest
        {
            Code = "print(42)",
            Files = [new ExecutionFile { Name = name, Content = [1, 2, 3] }]
        };
        ArgumentException exception = Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(request));
        Assert.Contains("reserved", exception.Message);
    }
}
