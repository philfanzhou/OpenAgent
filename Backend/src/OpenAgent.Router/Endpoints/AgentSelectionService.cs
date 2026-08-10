using Microsoft.Extensions.Options;
using OpenAgent.Router.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Endpoints;

internal sealed class AgentSelectionService(
    IIntentAgentSelector intentAgentSelector,
    IOptions<IntentRecognitionOptions> options) : IAgentSelectionService
{
    private readonly IntentRecognitionOptions _options = options.Value;

    public async Task<string?> SelectAsync(
        AgentSelectionRequest request,
        CancellationToken cancellationToken)
    {
        string? selectedAgentId = request.ExplicitAgentId;
        if (!string.IsNullOrWhiteSpace(selectedAgentId))
        {
            return selectedAgentId;
        }

        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            return null;
        }

        string? selection = _options.Enabled
            ? await intentAgentSelector.SelectAsync(request, cancellationToken).ConfigureAwait(false)
            : null;
        return selection ?? _options.FallbackAgentId;
    }
}
