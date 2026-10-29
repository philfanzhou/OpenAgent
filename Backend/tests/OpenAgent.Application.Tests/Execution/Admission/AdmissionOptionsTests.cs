using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenAgent.Application.Execution.Admission;
using OpenAgent.Application.Exten;
using Xunit;

namespace OpenAgent.Application.Tests.Execution.Admission;

public sealed class AdmissionOptionsTests
{
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
