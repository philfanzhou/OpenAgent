using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Application.Tests.Runtime;
using OpenAgent.Application.Tests.TestDoubles;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Capabilities.Tests.TestDoubles;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using Xunit;

namespace OpenAgent.Application.Tests.Execution;

public sealed class FileDeliveryExecutionTests
{
    private static readonly AgentRequest Request = new()
    {
        AgentId = "test-agent", LlmProfileId = "test-model", ConversationId = "delivery", Query = "Create the file."
    };
    private static readonly AgentUserContext User = new() { TenantId = "tenant-1", UserId = "user-1" };

    [Theory]
    [InlineData(true, "success")]
    [InlineData(true, "failed")]
    [InlineData(true, "cancelled")]
    [InlineData(true, "disconnected")]
    [InlineData(false, "success")]
    [InlineData(false, "failed")]
    [InlineData(false, "cancelled")]
    [InlineData(false, "disconnected")]
    public async Task ExecuteAsync_OnlyExplicitlyPublishedAssetsSurviveAsAttachments(bool publish, string outcome)
    {
        FileDeliveryRuntimeDriver driver = new(publish, outcome);
        RecordingFileAssetRepository repository = new();
        RecordingFileObjectStore objects = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = CreateRuntime(driver, repository, objects);
        if (outcome == "disconnected")
        {
            await foreach (AgentStreamEvent item in runtime.Executor.ExecuteStreamingAsync(Request, User, default))
            {
                if (item.Type == AgentStreamEventType.Content)
                {
                    break;
                }
            }
        }
        else if (outcome != "success")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.Executor.ExecuteAsync(Request, User, default));
        }
        else
        {
            await runtime.Executor.ExecuteAsync(Request, User, default);
        }
        ConversationRecord record = (await runtime.Store.GetRecordAsync("tenant-1", "delivery"))!;
        ConversationStatus expected = outcome == "success" ? ConversationStatus.Completed
            : outcome == "failed" ? ConversationStatus.Failed : ConversationStatus.Cancelled;
        Assert.Equal(expected, record.Status);
        ConversationMessage assistant = record.Messages.Last(message => message.Role == "assistant");
        Assert.Equal(publish ? new[] { driver.ExportedFileId! } : [], assistant.FileIds);
        Assert.Equal(publish ? 1 : 0, assistant.Metadata?.Files?.Count ?? 0);
        if (outcome != "success")
        {
            Assert.Equal(expected.ToString(), assistant.Metadata?.ExecutionStatus);
            if (outcome == "failed")
            {
                Assert.NotNull(assistant.Metadata?.Error);
            }
        }
        // The workspace lease is already gone; the original stored result remains readable.
        FileAssetService files = new(repository, objects, Options.Create(new FileAssetOptions { Enabled = true }));
        FileAssetContent content = await files.ReadAsync(driver.ExportedFileId!,
            new() { TenantId = "tenant-1", UserId = "user-1", ConversationId = "delivery" }, default);
        Assert.Equal(FileDeliveryRuntimeDriver.Result, content.Data);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CommitFails_DoesNotEmitCompletionAndPersistsOneFailedAttachmentMessage()
    {
        Mock<IConversationStore> store = new();
        ConversationRecord? record = null;
        int appends = 0;
        store.Setup(value => value.GetRecordAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => record);
        store.Setup(value => value.CreateAsync(It.IsAny<ConversationRecord>(), It.IsAny<CancellationToken>()))
            .Returns((ConversationRecord created, CancellationToken _) => { record = created; return Task.FromResult(true); });
        store.Setup(value => value.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<ConversationMessage>>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, int version, IReadOnlyList<ConversationMessage> messages, CancellationToken _) =>
            {
                if (++appends == 1)
                {
                    throw new IOException("Commit unavailable.");
                }
                record!.Messages.AddRange(messages);
                return Task.FromResult(AppendResult.Ok(version + 1, record!.Messages.Count));
            });
        store.Setup(value => value.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConversationStatus>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string _, ConversationStatus status, int _, CancellationToken _) =>
            {
                record!.Status = status;
                return Task.FromResult(true);
            });
        FileDeliveryRuntimeDriver driver = new();
        await using AgentExecutorUsageTests.TestRuntime runtime = CreateRuntime(driver, new(), new(), services =>
        {
            services.RemoveAll<IConversationStore>();
            services.AddSingleton(store.Object);
        });
        List<AgentStreamEvent> observed = [];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await foreach (AgentStreamEvent item in runtime.Executor.ExecuteStreamingAsync(Request, User, default))
            {
                observed.Add(item);
            }
        });
        Assert.DoesNotContain(observed, item => item.Type == AgentStreamEventType.Usage);
        Assert.Equal(ConversationStatus.Failed, record!.Status);
        ConversationMessage assistant = Assert.Single(record.Messages.Where(message => message.Role == "assistant" && !string.IsNullOrEmpty(message.Content)));
        Assert.Equal(new[] { driver.ExportedFileId! }, assistant.FileIds);
        Assert.NotNull(assistant.Metadata?.Error);
    }

    private static AgentExecutorUsageTests.TestRuntime CreateRuntime(FileDeliveryRuntimeDriver driver,
        RecordingFileAssetRepository repository, RecordingFileObjectStore objects, Action<IServiceCollection>? configure = null) =>
        AgentExecutorUsageTests.CreateRuntime(driver, configure: services =>
        {
            services.RemoveAll<IFileAssetRepository>();
            services.AddSingleton<IFileAssetRepository>(repository);
            services.AddSingleton<IFileObjectStore>(objects);
            services.AddSingleton<IOptions<FileAssetOptions>>(Options.Create(new FileAssetOptions { Enabled = true }));
            services.AddSingleton<IOptions<FileExchangeOptions>>(Options.Create(new FileExchangeOptions { Enabled = true }));
            configure?.Invoke(services);
        });
}
