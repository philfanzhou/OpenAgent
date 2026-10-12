using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Execution;
using OpenAgent.Core.Tests.Runtime;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Execution;

public class RuntimeRegistryTests
{
    [Fact]
    public void Resolve_UnknownBinding_RejectsWithoutFallback()
    {
        var adapter = new Mock<IRuntimeAdapter>();
        adapter.SetupGet(item => item.Id).Returns("acp");
        adapter.Setup(item => item.Describe()).Returns([new("acp", "first", "First"), new("acp", "second", "Second")]);
        var registry = new RuntimeRegistry([adapter.Object]);
        Assert.Same(adapter.Object, registry.Resolve(new RuntimeBinding { Adapter = "acp", Harness = "second" }));
        Assert.Throws<AgentException>(() => registry.Resolve(new RuntimeBinding { Adapter = "acp", Harness = "unknown" }));
        Assert.Equal(2, registry.List().Count);
    }

    [Fact]
    public async Task ExecuteAsync_RegisteredAdapter_ReceivesAuthorizedResolvedInvocation()
    {
        RuntimeInvocation template = AcpRuntimeAdapterTests.Invocation("hello");
        template.Profile.Config.Runtime = new RuntimeBinding { Adapter = "custom" };
        var resolver = new Mock<IAgentRuntimeResolver>();
        resolver.Setup(item => item.ResolveAsync(It.IsAny<string>(), "preview", It.IsAny<IAgentUserContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(template.Profile);
        var adapter = new Mock<IRuntimeAdapter>();
        adapter.SetupGet(item => item.Id).Returns("custom");
        adapter.Setup(item => item.Describe()).Returns([new("custom", null, "Custom harness")]);
        adapter.Setup(item => item.ExecuteAsync(It.IsAny<RuntimeInvocation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentResponse { Content = "custom runtime" });
        var provider = new FakeChatProvider([]);
        await using var runtime = AgentExecutorUsageTests.CreateRuntime(provider, configure: services =>
        {
            services.RemoveAll<IAgentRuntimeResolver>();
            services.AddSingleton(resolver.Object);
            services.AddSingleton(adapter.Object);
        });
        AgentResponse response = await runtime.Executor.ExecuteAsync(template.Request, template.User, CancellationToken.None);
        Assert.Equal("custom runtime", response.Content);
        resolver.Verify(item => item.ResolveAsync(It.IsAny<string>(), "preview", template.User, It.IsAny<CancellationToken>()), Times.Once);
        adapter.Verify(item => item.ExecuteAsync(It.Is<RuntimeInvocation>(call =>
            call.Turn.TenantId == "tenant" && call.Turn.UserId == "user" && call.Request.LlmProfileId == "preview"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
