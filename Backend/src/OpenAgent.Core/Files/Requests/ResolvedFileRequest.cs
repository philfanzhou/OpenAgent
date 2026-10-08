using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Files.Requests;

internal sealed class ResolvedFileRequest
{
    public required AgentRequest Request { get; init; }
    public required IReadOnlyList<FileAsset> Files { get; init; }
}
