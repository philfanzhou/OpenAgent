using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Application.Execution.Admission;
using OpenAgent.Application.Exten;
using Xunit;

namespace OpenAgent.Application.Tests.Execution.Admission;

public sealed class AdmissionOptionsTests
{
    [Fact]
    public void Bind_Unconfigured_QueuesSixteenAndKeepsTheRuntimeConcurrencyOfEight()
    {
        ServiceCollection services = new();
        services.AddAgentApplication(new ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        AdmissionOptions options = provider.GetRequiredService<IOptions<AdmissionOptions>>().Value;

        Assert.Equal(16, options.MaxQueuedTurns);
        Assert.Equal(16, options.MaxQueuedPerTenant);
        // 准入不额外压低现有吞吐：两级并发上限都与运行时的 OpenCode:MaxConcurrentTurns 默认值相同。
        Assert.Equal(8, options.MaxConcurrentTurns);
        Assert.Equal(8, options.MaxConcurrentPerTenant);
        Assert.Equal(120, options.QueueTimeoutSeconds);
    }

    [Theory]
    [InlineData("MaxConcurrentTurns", "0")]
    [InlineData("MaxConcurrentTurns", "129")]
    [InlineData("MaxConcurrentPerTenant", "129")]
    [InlineData("MaxQueuedTurns", "-1")]
    [InlineData("MaxQueuedPerTenant", "4097")]
    [InlineData("QueueTimeoutSeconds", "0")]
    [InlineData("ExecutionTimeoutSeconds", "0")]
    [InlineData("RetryAfterSeconds", "0")]
    public void Bind_InvalidAdmissionLimits_RejectsConfiguration(string field, string value)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RuntimeAdmission:" + field] = value }).Build();
        ServiceCollection services = new();
        services.AddAgentApplication(config);
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AdmissionOptions>>().Value);
    }
}
