using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Abstract;

public interface IToolRegistry
{
    void RegisterTool(SkillDescriptor tool, Func<Dictionary<string, object>, CancellationToken, Task<string>> executor);

    IReadOnlyList<SkillDescriptor> GetTools();

    Task<string> ExecuteToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken cancellationToken = default);

    bool HasTool(string toolName);
}
