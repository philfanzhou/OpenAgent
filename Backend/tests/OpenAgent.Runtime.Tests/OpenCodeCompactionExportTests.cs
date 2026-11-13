using System.Text.Json;
using OpenAgent.Runtime.OpenCode;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using Xunit;

namespace OpenAgent.Runtime.Tests;

public sealed class OpenCodeCompactionExportTests
{
    [OpenCodeUnixFact]
    public async Task ReadAsync_LargeExport_UsesRegularFileAndCleansItAfterParseFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "export test ' $ & % ! " + Guid.NewGuid().ToString("N"));
        OpenCodeWorkspace.CreatePrivateDirectory(root);
        string executable = Path.Combine(root, "export helper ' $ & % !");
        try
        {
            // A producer that drops its pipe output reproduces the v1 behavior independent of pipe capacity.
            // The command and output paths contain shell metacharacters to verify positional argument handling.
            await File.WriteAllTextAsync(executable, "#!/bin/sh\nif [ -p /dev/stdout ]; then printf '{'; else cat \"$0.json\"; fi\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            string json = JsonSerializer.Serialize(new
            {
                info = new { id = "session" },
                padding = new string('x', 256 * 1024),
                messages = new[] { new
                {
                    info = new { role = "assistant", summary = true, id = "compaction", time = new { completed = 1 } },
                    parts = new[] { new { type = "text", text = "retained from large export" } }
                } }
            });
            await File.WriteAllTextAsync(executable + ".json", json);
            RuntimeInvocation invocation = new(new AgentRuntimeProfile
            {
                AgentId = "agent", Config = new(),
                Model = new LlmConfig { ModelId = "fixture", Endpoint = "http://127.0.0.1:9/v1", ApiKey = "local-test" }
            }, new TurnContext { TenantId = "tenant", UserId = "user", ConversationId = "conversation", TraceId = "trace" }, "", [], []);
            OpenCodeAgentProcess agent = new(null!, null!, null!, "key", root);
            var audit = Assert.Single(await OpenCodeCompactionExport.ReadAsync(new(executable, []), invocation, agent, [], "session", CancellationToken.None));
            Assert.Equal("retained from large export", audit.Summary);
            Assert.Empty(Directory.GetFiles(root, "compaction-export-*", SearchOption.AllDirectories));
            await File.WriteAllTextAsync(executable + ".json", "{invalid json");
            await Assert.ThrowsAnyAsync<JsonException>(() => OpenCodeCompactionExport.ReadAsync(
                new(executable, []), invocation, agent, [], "session", CancellationToken.None));
            Assert.Empty(Directory.GetFiles(root, "compaction-export-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [OpenCodeUnixTheory]
    [InlineData("exit")]
    [InlineData("oversized")]
    [InlineData("cancel")]
    public async Task ReadAsync_ExportDoesNotComplete_CleansPrivateOutput(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "export-cleanup-" + Guid.NewGuid().ToString("N"));
        OpenCodeWorkspace.CreatePrivateDirectory(root);
        try
        {
            string executable = Path.Combine(root, "export-helper");
            string body = scenario switch
            {
                "exit" => "printf '{'; exit 7",
                "oversized" => "dd if=/dev/zero bs=1048576 count=33 2>/dev/null",
                _ => "printf '{'; exec sleep 30"
            };
            await File.WriteAllTextAsync(executable, "#!/bin/sh\n" + body + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RuntimeInvocation invocation = new(new AgentRuntimeProfile
            {
                AgentId = "agent", Config = new(),
                Model = new LlmConfig { ModelId = "fixture", Endpoint = "http://127.0.0.1:9/v1", ApiKey = "local-test" }
            }, new TurnContext { TenantId = "tenant", UserId = "user", ConversationId = "conversation", TraceId = "trace" }, "", [], []);
            OpenCodeAgentProcess agent = new(null!, null!, null!, "key", root);
            using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(scenario == "cancel" ? 250 : 10000));
            Task export = OpenCodeCompactionExport.ReadAsync(new(executable, []), invocation, agent, [], "session", cancel.Token);
            if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
            else await Assert.ThrowsAnyAsync<IOException>(() => export);
            Assert.Empty(Directory.GetFiles(root, "compaction-export-*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadSummaries_NativeMetadata_ExcludesNormalRepliesAndDoesNotRepeatCompletedAudits()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"info":{"id":"session"},"messages":[
              {"info":{"id":"user","role":"user"},"parts":[]},
              {"info":{"id":"reply","role":"assistant","time":{"completed":1}},"parts":[{"type":"text","text":"normal reply"}]},
              {"info":{"id":"summary","role":"assistant","summary":true,"time":{"completed":1}},"parts":[{"type":"text","text":"retained"},{"type":"reasoning","text":"hidden"}]}
            ]}
            """);
        HashSet<string> reported = [];
        var summary = Assert.Single(OpenCodeCompactionExport.ReadSummaries(document.RootElement, "session", reported));
        Assert.Equal("retained", summary.Summary);
        Assert.Equal("Succeeded", summary.Status);
        Assert.True(summary.IsRuntimeEvent);
        Assert.Empty(summary.CompactedMessages);
        Assert.Empty(OpenCodeCompactionExport.ReadSummaries(document.RootElement, "session", reported));
        Assert.Throws<InvalidOperationException>(() => OpenCodeCompactionExport.ReadSummaries(document.RootElement, "other-session", []));
    }
}
