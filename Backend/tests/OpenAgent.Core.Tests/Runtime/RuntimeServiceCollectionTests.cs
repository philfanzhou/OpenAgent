using Microsoft.Extensions.DependencyInjection;
using OpenAgent.Contracts.Approvals;
using OpenAgent.Core.Exten;
using Xunit;

namespace OpenAgent.Core.Tests.Runtime;

public class RuntimeServiceCollectionTests
{
    [Fact]
    public void AddRuntimeServices_RegistersHumanApprovalServiceInterface()
    {
        // 决策端点注入 IHumanApprovalService：接口缺失注册会直接 500。
        ServiceCollection services = [];
        services.AddRuntimeServices();

        ServiceDescriptor? descriptor = services.FirstOrDefault(
            service => service.ServiceType == typeof(IHumanApprovalService));
        Assert.NotNull(descriptor);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
