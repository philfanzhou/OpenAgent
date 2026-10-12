using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Contracts.Runtime;

public sealed record RuntimeInvocation(
    AgentRequest Request,
    AgentRuntimeProfile Profile,
    TurnContext Turn,
    IAgentUserContext User,
    IReadOnlyList<FileAsset> Files);
