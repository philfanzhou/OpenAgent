using System.Text.Json;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Client;
using OpenAgent.Runtime.Acp.Processes;
using OpenAgent.Runtime.OpenCode;
using Xunit;

namespace OpenAgent.Runtime.Tests;

[Collection(RealOpenCodeCollection.Name)]
public sealed class OpenCodeIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "n02-canary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProbeProcess_ReceivesPrivateProfileTempAndCwdWithoutHostCanary()
    {
        const string variable = "OPENAGENT_N02_HOST_CANARY";
        string? previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "N02_HOST_ONLY_VALUE");
        try
        {
            OpenCodeWorkspace.CreatePrivateDirectory(_root);
            OpenCodeLaunchSpec probe = OperatingSystem.IsWindows()
                ? new("cmd.exe", ["/d", "/c", "echo %HOME%&echo %USERPROFILE%&echo %TEMP%&echo %OPENAGENT_N02_HOST_CANARY%&cd"])
                : new("/bin/sh", ["-c", "printf '%s\\n' \"$HOME\" \"$USERPROFILE\" \"$TEMP\" \"${OPENAGENT_N02_HOST_CANARY:-absent}\" \"$PWD\""]);
            AcpProcessOptions launch = OpenCodeProcess.Create(probe, Request("http://127.0.0.1:9/v1"), [], _root);
            await using AcpProcess process = AcpProcess.Start(launch);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            List<string> lines = [];
            while (await process.Transport.ReadAsync(deadline.Token) is { } line)
            {
                lines.Add(System.Text.Encoding.UTF8.GetString(line).Trim());
            }
            Assert.Equal(5, lines.Count);
            Assert.Equal(launch.Environment!["HOME"], lines[0]);
            Assert.Equal(lines[0], lines[1]);
            Assert.Equal(launch.Environment["TEMP"], lines[2]);
            Assert.DoesNotContain("N02_HOST_ONLY_VALUE", lines);
            // pwd may canonicalize a system temp symlink (e.g. /var on macOS).
            Assert.Equal(Path.GetFileName(_root), Path.GetFileName(lines[4]));
            Assert.Equal(0, await process.Exit.WaitAsync(deadline.Token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [OpenCodeFact]
    public async Task ExternalAndAncestorCanaries_ControlLoadsThemAndHardenedLaunchExcludesThem()
    {
        string host = Path.Combine(_root, "synthetic-host");
        Write(host, ".claude/skills/host-claude/SKILL.md", Skill("host-claude", "N02_CLAUDE_CANARY"));
        Write(host, ".agents/skills/host-agents/SKILL.md", Skill("host-agents", "N02_AGENTS_CANARY"));
        Write(host, ".claude/CLAUDE.md", "N02_PROMPT_CANARY");
        Write(_root, "AGENTS.md", "N02_ANCESTOR_CANARY");
        string ancestor = Write(_root, "ancestor.txt", "N02_CONFIG_CANARY");
        Write(_root, "opencode.json", JsonSerializer.Serialize(new { instructions = new[] { ancestor } }));
        // A stale host XDG config is present but the launched process receives private XDG paths.
        Write(host, ".config/opencode/AGENTS.md", "N02_XDG_CANARY");

        string? observed = null;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            observed = request.GetRawText();
            await ModelFixture.TextAsync(context, "ok");
        });
        RuntimeInvocation invocation = Request(model.Endpoint);
        string control = Path.Combine(_root, "control");
        await PromptAsync(invocation, control, host, hardened: false);
        Assert.NotNull(observed);
        foreach (string marker in new[] { "N02_CLAUDE_CANARY", "N02_AGENTS_CANARY", "N02_PROMPT_CANARY", "N02_ANCESTOR_CANARY", "N02_CONFIG_CANARY" })
        {
            Assert.Contains(marker, observed);
        }
        observed = null;
        await PromptAsync(invocation, Path.Combine(_root, "hardened"), host, hardened: true);
        Assert.NotNull(observed);
        foreach (string marker in new[] { "N02_CLAUDE_CANARY", "N02_AGENTS_CANARY", "N02_PROMPT_CANARY", "N02_ANCESTOR_CANARY", "N02_CONFIG_CANARY", "N02_XDG_CANARY" })
        {
            Assert.DoesNotContain(marker, observed);
        }
    }

    [OpenCodeFact]
    public async Task DirtyIdentityHome_StillLoadsOpenCodeConfig_RequiresCleanServiceIdentity()
    {
        string host = Path.Combine(_root, "dirty-identity");
        string identity = Write(host, ".opencode/identity.txt", "N02_DIRTY_IDENTITY_CANARY");
        Write(host, ".opencode/opencode.json", JsonSerializer.Serialize(new { instructions = new[] { identity } }));
        bool injected = false;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            injected |= request.GetRawText().Contains("N02_DIRTY_IDENTITY_CANARY", StringComparison.Ordinal);
            await ModelFixture.TextAsync(context, "ok");
        });
        await PromptAsync(Request(model.Endpoint), Path.Combine(_root, "process"), host, hardened: true);
        // This counterexample must remain visible: HOME flags alone do not provide isolation.
        Assert.True(injected);
    }

    [OpenCodeFact]
    public async Task DefaultNativePolicy_RejectsForcedReadOfOutsideCanary()
    {
        string outside = Write(_root, "outside.txt", "N02_OUTSIDE_FILE_CANARY");
        int step = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            Assert.DoesNotContain("N02_OUTSIDE_FILE_CANARY", request.GetRawText());
            if (step++ == 0)
            {
                if (request.TryGetProperty("tools", out JsonElement tools))
                {
                    Assert.DoesNotContain(tools.EnumerateArray(), tool =>
                        tool.GetProperty("function").GetProperty("name").GetString() == "read");
                }
                await ModelFixture.ChunkAsync(context, new
                {
                    role = "assistant",
                    tool_calls = new[] { new { index = 0, id = "outside-read", type = "function", function = new
                    {
                        name = "read", arguments = JsonSerializer.Serialize(new { filePath = outside })
                    } } }
                }, "tool_calls");
            }
            else
            {
                await ModelFixture.TextAsync(context, "rejected");
            }
        });
        await using RuntimeFixture runtime = new(model.Endpoint);
        await runtime.CollectAsync(runtime.Request);
        Assert.True(step >= 2);
        Assert.Equal("N02_OUTSIDE_FILE_CANARY", File.ReadAllText(outside));
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeFact]
    public async Task SameConversationName_DifferentTenantOrUser_DoesNotShareProcessOrSkill()
    {
        string[] packages = [Path.Combine(_root, "a"), Path.Combine(_root, "b"), Path.Combine(_root, "c")];
        string[] markers = ["N02_TENANT_A_SKILL", "N02_TENANT_B_SKILL", "N02_OTHER_USER_SKILL"];
        for (int i = 0; i < packages.Length; i++)
        {
            Write(packages[i], "SKILL.md", Skill("same-skill-name", markers[i]));
        }
        int step = 0;
        await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
        {
            int current = step++;
            Assert.Contains(markers[current], request.GetRawText());
            for (int i = 0; i < markers.Length; i++)
            {
                if (i != current)
                {
                    Assert.DoesNotContain(markers[i], request.GetRawText());
                }
            }
            await ModelFixture.TextAsync(context, "ok");
        });
        await using RuntimeFixture runtime = new(model.Endpoint, settings => settings["OpenCode:KeepAliveSeconds"] = "120");
        await runtime.CollectAsync(runtime.Request with { Skills = [new("same-skill-name", packages[0])] });
        await runtime.CollectAsync(runtime.Request with
        {
            Skills = [new("same-skill-name", packages[1])],
            Turn = new() { TenantId = "other-tenant", UserId = "user", ConversationId = "conversation", TraceId = "n02-b" }
        });
        await runtime.CollectAsync(runtime.Request with
        {
            Skills = [new("same-skill-name", packages[2])],
            Turn = new() { TenantId = "tenant", UserId = "other-user", ConversationId = "conversation", TraceId = "n02-c" }
        });
        Assert.Equal(3, runtime.Keeper.KeptCount);
        Assert.Equal(0, runtime.Keeper.ReusedCount);
        await runtime.Provider.DisposeAsync();
        runtime.AssertWorkspacesReleased();
    }

    [OpenCodeLinuxFact]
    public async Task ExplicitBashAndExternalDirectoryAllowance_CanReadOutsideFileAndSeeSiblingProcess_IsNotOsIsolation()
    {
        string outside = Write(_root, "outside.txt", "N02_FILE_AND_PROCESS_VISIBLE");
        using System.Diagnostics.Process sibling = System.Diagnostics.Process.Start("sleep", "60")!;
        try
        {
            string command = "test -d /proc/" + sibling.Id + " && cat '" + outside.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            bool resultSeen = false;
            await using ModelFixture model = await ModelFixture.StartAsync(async (context, request) =>
            {
                if (request.GetProperty("messages").EnumerateArray().Any(message => message.GetProperty("role").GetString() == "tool"))
                {
                    Assert.Contains("N02_FILE_AND_PROCESS_VISIBLE", request.GetRawText());
                    resultSeen = true;
                    await ModelFixture.TextAsync(context, "boundary verified");
                }
                else
                {
                    await ModelFixture.ChunkAsync(context, new
                    {
                        role = "assistant", tool_calls = new[] { new { index = 0, id = "boundary", type = "function",
                            function = new { name = "bash", arguments = JsonSerializer.Serialize(new { command }) } } }
                    }, "tool_calls");
                }
            });
            await using RuntimeFixture runtime = new(model.Endpoint, settings =>
            {
                settings["OpenCode:AllowedNativeTools:0"] = "bash";
                settings["OpenCode:AllowedNativeTools:1"] = "external_directory";
            });
            await runtime.CollectAsync(runtime.Request);
            Assert.True(resultSeen);
            runtime.AssertWorkspacesReleased();
        }
        finally
        {
            if (!sibling.HasExited)
            {
                sibling.Kill(entireProcessTree: true);
            }
            await sibling.WaitForExitAsync();
        }
    }

    [OpenCodeUnixFact]
    public void PrivateStorage_LinkedOwnerDirectory_RejectsBeforeCreatingTurn()
    {
        Directory.CreateDirectory(_root);
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(_root, "owner");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<IOException>(() => OpenCodeWorkspace.CreatePrivateDirectory(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [OpenCodeUnixFact]
    public void PrivateStorage_UnixDirectory_HasNoGroupOrOtherAccess()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The attribute reports a skip; this guard also informs platform analyzers.
        }
        OpenCodeWorkspace.CreatePrivateDirectory(_root);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_root));
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
        Assert.Throws<IOException>(() => OpenCodeWorkspace.CreatePrivateDirectory(_root));
    }

    private async Task PromptAsync(RuntimeInvocation invocation, string processHome, string syntheticHost, bool hardened)
    {
        string workspace = Path.Combine(processHome, "workspace");
        OpenCodeWorkspace.CreatePrivateDirectory(workspace);
        AcpProcessOptions launch = OpenCodeProcess.Create(
            new(Environment.GetEnvironmentVariable("OPENAGENT_TEST_OPENCODE")!, ["acp"]), invocation, ["skill"], processHome);
        Dictionary<string, string> environment = new(launch.Environment!);
        // Test-only upstream hook models an OS homedir which ignores HOME/USERPROFILE (Windows).
        // Production must use a clean service identity, never this hook.
        environment["OPENCODE_TEST_HOME"] = syntheticHost;
        environment["OPENCODE_TEST_MANAGED_CONFIG_DIR"] = Path.Combine(_root, "empty-managed");
        if (!hardened)
        {
            environment.Remove("OPENCODE_DISABLE_EXTERNAL_SKILLS");
            environment.Remove("OPENCODE_DISABLE_CLAUDE_CODE");
            environment.Remove("OPENCODE_DISABLE_PROJECT_CONFIG");
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
        await using AcpProcess process = AcpProcess.Start(launch with { Environment = environment });
        await using AcpClient client = new(process.Transport);
        await client.InitializeAsync(new("n02-canary", "1"), deadline.Token);
        await using AcpSession session = await client.NewSessionAsync(new(workspace, []), deadline.Token);
        await using AcpPromptTurn prompt = await session.StartPromptAsync([new Acp.Protocol.TextContent("hello")], deadline.Token);
        await prompt.Completion.WaitAsync(deadline.Token);
    }

    private static RuntimeInvocation Request(string endpoint) => new(new AgentRuntimeProfile
    {
        AgentId = "n02", Config = new(),
        Model = new LlmConfig { ModelId = "fixture", Endpoint = endpoint, ApiKey = "local-test", ContextTokens = 100000 }
    }, new() { TenantId = "tenant", UserId = "user", ConversationId = "same-name", TraceId = "n02" },
        "Answer the user.", [new("user", "hello")], []);

    private static string Skill(string name, string description) => $"---\nname: {name}\ndescription: {description}\n---\nTest skill.\n";

    private static string Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
