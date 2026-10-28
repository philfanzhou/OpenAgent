using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Tooling.Abstractions;

/// <summary>能力工具（AIFunction 包装）向执行层暴露自己声明的并发类别。</summary>
internal interface IToolConcurrencyProvider
{
    ToolConcurrency Concurrency { get; }
}
