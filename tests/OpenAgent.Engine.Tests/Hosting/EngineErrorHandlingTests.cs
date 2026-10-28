using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Engine.Host;
using OpenAgent.Hosting;
using OpenAgent.Hosting.Errors;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class EngineErrorHandlingTests
{
    [Fact]
    public void Map_ClientResultException_DoesNotLeakRawProviderBody()
    {
        var mapper = CreateEngineMapper();
        // 复现真实事故形态：括号内为 provider 错误码，正文含账户标识与密钥材料。
        string rawProviderBody =
            "HTTP 429 (exceeded_current_quota_error: )\n\n"
            + "Your account org-84da65729db84e65 is suspended due to insufficient balance, "
            + "key <ak-fbes9xkyrfei11baoz31>";
        var exception = new ClientResultException(rawProviderBody, (PipelineResponse)null!, null!);

        var (_, problem) = mapper.Map(exception, "trace-1", "/api/v1/agent/chat");

        string payload = JsonSerializer.Serialize(problem);
        Assert.DoesNotContain("org-84da65729db84e65", payload);
        Assert.DoesNotContain("ak-fbes9xkyrfei11baoz31", payload);
        // 载荷只保留脱敏摘要，instance 不再承载 provider 原始错误体。
        Assert.Null(problem.Instance);
        Assert.StartsWith("模型服务返回错误", problem.Detail);
    }

    [Fact]
    public void Map_ClientResultException_KeepsSanitizedSummary()
    {
        var mapper = CreateEngineMapper();
        var exception = new ClientResultException(
            "HTTP 429 (exceeded_current_quota_error: )",
            (PipelineResponse)null!,
            null!);

        var (_, problem) = mapper.Map(exception, "trace-1", "/api/v1/agent/chat");

        Assert.Equal("ProviderRequestFailed", problem.Title);
        Assert.StartsWith("模型服务返回错误", problem.Detail);
        Assert.Contains("exceeded_current_quota_error", problem.Detail);
        Assert.Equal(
            (int)OpenAgent.Contracts.Requests.AgentErrorCode.DependencyUnavailable,
            (int)problem.Extensions["errorCode"]!);
    }

    private static AgentExceptionMapper CreateEngineMapper()
    {
        var services = new ServiceCollection();
        services.AddEngineErrorHandling();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AgentExceptionHandlingOptions>>();
        return new AgentExceptionMapper(options);
    }
}
