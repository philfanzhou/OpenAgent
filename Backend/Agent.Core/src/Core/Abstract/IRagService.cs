using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Abstract;

public interface IRagService
{
    Task IndexDocumentAsync(
        string content,
        Dictionary<string, object>? metadata,
        string? ragInstanceId,
        RagConfig config,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default);

    Task<List<string>> SearchAsync(
        string query,
        int limit,
        RagConfig config,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default);

    Task<List<SearchResult>> SearchDetailedAsync(
        string query,
        int limit,
        RagConfig config,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default);
}
