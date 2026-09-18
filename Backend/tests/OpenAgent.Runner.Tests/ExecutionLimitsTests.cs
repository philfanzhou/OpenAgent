using OpenAgent.Contracts.Execution;
using Xunit;

namespace OpenAgent.Runner.Tests;

public class ExecutionLimitsTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("folder/../file.txt")]
    [InlineData("folder//file.txt")]
    [InlineData("folder/")]
    [InlineData("main.py")]
    [InlineData("main.mjs")]
    public void Validate_RejectsPathTraversalAndReservedScript(string name)
    {
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)", Files = [new ExecutionFile { Name = name, Content = [1] }]
        }));
    }

    [Fact]
    public void Validate_AllowsRelativeSubPathsForPackageMounts()
    {
        // Whole skill packages mount with names relative to the package root, so
        // slash-separated sub-paths are legal as long as every segment is safe.
        ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)",
            Files = [new ExecutionFile { Name = "lib/helper.py", Content = [1] }]
        });
    }

    [Fact]
    public void Validate_CustomEntryReplacesDefaultReservation()
    {
        var request = new CodeExecutionRequest
        {
            Code = "print(42)",
            EntryFileName = "openagent_skill_entry__.py",
            Files = [new ExecutionFile { Name = "main.py", Content = [1] }]
        };
        ExecutionLimits.Validate(request);
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)",
            EntryFileName = "main.mjs",
            Files = [new ExecutionFile { Name = "main.mjs", Content = [1] }]
        }));
    }

    [Theory]
    [InlineData("nested/entry.py")]
    [InlineData("entry.txt")]
    [InlineData("entry.mjs")]
    public void Validate_RejectsInvalidCustomEntries(string entry)
    {
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)", EntryFileName = entry
        }));
    }

    [Theory]
    [InlineData("bad key")]
    [InlineData("../escape")]
    [InlineData("key/with/slash")]
    public void Validate_RejectsUnsafeSessionKeys(string key)
    {
        Assert.Throws<ArgumentException>(() => ExecutionLimits.Validate(new CodeExecutionRequest
        {
            Code = "print(42)", SessionKey = key
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
