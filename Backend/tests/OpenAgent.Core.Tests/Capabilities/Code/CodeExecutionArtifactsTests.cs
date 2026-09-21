using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Capabilities.Code;

public class CodeExecutionArtifactsTests
{
    [Fact]
    public async Task PublishAsync_MixedOutputs_RegistersStorableAndSkipsRest()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = CreateFiles(repository, objects);
        CodeExecutionResult result = Result(
            new ExecutionFile { Name = "report.html", Content = "<h1>hi</h1>"u8.ToArray() },
            new ExecutionFile { Name = "style.css", Content = "body{}"u8.ToArray() },
            new ExecutionFile { Name = "helper.py", Content = "print(1)"u8.ToArray() },
            new ExecutionFile { Name = "empty.txt", Content = [] });

        CodeExecutionArtifacts.PublishResult publish = await CodeExecutionArtifacts.PublishAsync(
            result, files, Scope(), CancellationToken.None);

        Assert.Equal(2, publish.Files.Count);
        string[] registeredNames = ["report.html", "style.css"];
        Assert.Equal(registeredNames,
            repository.Assets.Values.Select(asset => asset.FileName).OrderBy(name => name).ToArray());
        Assert.Equal("text/html", repository.Assets.Values.Single(a => a.FileName == "report.html").MediaType);
        Assert.Equal("text/css", repository.Assets.Values.Single(a => a.FileName == "style.css").MediaType);
        string[] skippedNames = ["empty.txt", "helper.py"];
        Assert.Equal(skippedNames, publish.Skipped.Select(skipped => skipped.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task PublishAsync_StorageRejectsNarrowedWhitelist_SkipsAndReportsReason()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService files = new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128,
                AllowedExtensions = [".txt"]
            }));
        CodeExecutionResult result = Result(
            new ExecutionFile { Name = "report.html", Content = "<h1>hi</h1>"u8.ToArray() },
            new ExecutionFile { Name = "notes.txt", Content = "ok"u8.ToArray() });

        CodeExecutionArtifacts.PublishResult publish = await CodeExecutionArtifacts.PublishAsync(
            result, files, Scope(), CancellationToken.None);

        Assert.Single(publish.Files);
        string[] registeredNames = ["notes.txt"];
        Assert.Equal(registeredNames, repository.Assets.Values.Select(asset => asset.FileName).ToArray());
        CodeExecutionArtifacts.SkippedFile skipped = Assert.Single(publish.Skipped);
        Assert.Equal("report.html", skipped.Name);
        Assert.Contains("not allowed", skipped.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static IFileAssetService CreateFiles(
        RecordingFileAssetRepository repository,
        RecordingFileObjectStore objects) => new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128
            }));

    private static FileAssetScope Scope() => new()
    {
        TenantId = "tenant-a",
        UserId = "user-a",
        ConversationId = "conversation-a"
    };

    private static CodeExecutionResult Result(params ExecutionFile[] files) => new()
    {
        ExecutionId = "exec-a",
        Files = [.. files]
    };
}
