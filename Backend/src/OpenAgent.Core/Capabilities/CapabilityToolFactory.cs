using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities;

internal sealed class CapabilityToolFactory
{
    private readonly IReadOnlyList<ICapabilitySource> _sources;
    private readonly AgentAuthorizationGate _authorization;

    public CapabilityToolFactory(
        IEnumerable<ICapabilitySource> sources,
        AgentAuthorizationGate authorization)
    {
        _sources = sources.ToList().AsReadOnly();
        _authorization = authorization;
    }

    internal async Task<IReadOnlyList<AITool>> CreateAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken) =>
        (await CreateRuntimeAsync(
            agentId,
            config,
            user,
            cancellationToken).ConfigureAwait(false)).Tools;

    internal async Task<CapabilityToolRuntime> CreateRuntimeAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        List<AITool> tools = [];
        Dictionary<string, ApprovalTarget> approvalTargets = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ICapabilitySource source in _sources)
        {
            IReadOnlyList<CapabilityDefinition> definitions = await source.DiscoverAsync(
                agentId,
                config,
                user,
                cancellationToken).ConfigureAwait(false);
            foreach (CapabilityDefinition definition in definitions)
            {
                if (await IsAvailableAsync(
                    agentId,
                    definition,
                    user,
                    cancellationToken).ConfigureAwait(false))
                {
                    if (!names.Add(definition.Name))
                    {
                        throw new InvalidOperationException(
                            $"Duplicate capability runtime name: {definition.Name}");
                    }
                    AIFunction function = new CapabilityAIFunction(definition);
                    tools.Add(definition.RequiresHumanApproval
                        ? new ApprovalRequiredAIFunction(function)
                        : function);
                    if (definition.RequiresHumanApproval)
                    {
                        approvalTargets.Add(definition.Name, new ApprovalTarget(
                            definition.ResourceType,
                            definition.ResourceId,
                            definition.ApprovalAction));
                    }
                }
            }
        }

        return new CapabilityToolRuntime(
            tools.AsReadOnly(),
            approvalTargets.AsReadOnly());
    }

    private async Task<bool> IsAvailableAsync(
        string agentId,
        CapabilityDefinition definition,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAvailableAsync(
            agentId,
            definition.ResourceType,
            definition.ResourceId,
            user,
            cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(definition.ParentResourceId)
            && !await _authorization.IsAvailableAsync(
                agentId,
                definition.ResourceType,
                definition.ParentResourceId,
                user,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await _authorization.IsAvailableAsync(
            agentId,
            AgentResourceType.Tool,
            definition.Name,
            user,
            cancellationToken).ConfigureAwait(false)
            && await _authorization.IsAvailableAsync(
                agentId,
                AgentResourceType.Function,
                definition.Name,
                user,
                cancellationToken).ConfigureAwait(false);
    }

    private sealed class CapabilityAIFunction : AIFunction
    {
        private readonly CapabilityDefinition _definition;
        private readonly JsonElement _schema;

        internal CapabilityAIFunction(CapabilityDefinition definition)
        {
            _definition = definition;
            using JsonDocument schema = JsonDocument.Parse(NormalizeSchema(definition.ParametersJsonSchema));
            _schema = schema.RootElement.Clone();
        }

        public override string Name => _definition.Name;
        public override string Description => _definition.Description;
        public override JsonElement JsonSchema => _schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, object?> values = arguments.ToDictionary(
                item => item.Key,
                item => item.Value);
            return await _definition.Invoke(values, cancellationToken).ConfigureAwait(false);
        }

        private static string NormalizeSchema(string? schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                return "{\"type\":\"object\"}";
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(schema);
                return document.RootElement.GetRawText();
            }
            catch (JsonException)
            {
                return "{\"type\":\"object\"}";
            }
        }
    }
}

internal sealed record CapabilityToolRuntime(
    IReadOnlyList<AITool> Tools,
    IReadOnlyDictionary<string, ApprovalTarget> ApprovalTargets);
