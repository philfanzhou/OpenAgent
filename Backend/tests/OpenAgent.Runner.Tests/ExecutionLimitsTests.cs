using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class ExecutionLimitsTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("main.py")]
    public void Validate_RejectsPathTraversalAndReservedScript(string name)
    {
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)", Files = [new ExecutionFile { Name = name, Content = [1] }]
        }));
    }

    [Fact]
    public void Validate_RejectsOversizedAndDuplicateInputs()
    {
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)", Files = [new ExecutionFile { Name = "data.txt", Content = new byte[ExecutionLimits.MaxFileBytes + 1] }]
        }));
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)", Files = [new ExecutionFile { Name = "data.txt" }, new ExecutionFile { Name = "DATA.txt" }]
        }));
    }
}
