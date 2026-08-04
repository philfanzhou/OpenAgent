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
        CancellationToken cancellationToken)
    {
        List<AITool> tools = [];
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
                if (await CanDiscoverAsync(
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
                    tools.Add(new AuthorizedAIFunction(
                        agentId,
                        definition,
                        user,
                        _authorization));
                }
            }
        }

        return tools.AsReadOnly();
    }

    private async Task<bool> CanDiscoverAsync(
        string agentId,
        CapabilityDefinition definition,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        if (!await _authorization.IsAuthorizedAsync(
            agentId,
            definition.ResourceType,
            definition.ResourceId,
            "discover",
            user,
            cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await _authorization.IsAuthorizedAsync(
                agentId,
                AgentResourceType.Tool,
                definition.Name,
                "discover",
                user,
                cancellationToken).ConfigureAwait(false)
            && await _authorization.IsAuthorizedAsync(
                agentId,
                AgentResourceType.Function,
                definition.Name,
                "discover",
                user,
                cancellationToken).ConfigureAwait(false);
    }

    private sealed class AuthorizedAIFunction : AIFunction
    {
        private readonly string _agentId;
        private readonly CapabilityDefinition _definition;
        private readonly IAgentUserContext _user;
        private readonly AgentAuthorizationGate _authorization;
        private readonly JsonElement _schema;

        internal AuthorizedAIFunction(
            string agentId,
            CapabilityDefinition definition,
            IAgentUserContext user,
            AgentAuthorizationGate authorization)
        {
            _agentId = agentId;
            _definition = definition;
            _user = user;
            _authorization = authorization;
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
            await EnsureExecuteAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, object?> values = arguments.ToDictionary(
                item => item.Key,
                item => item.Value);
            return await _definition.Invoke(values, cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureExecuteAsync(CancellationToken cancellationToken)
        {
            await _authorization.EnsureAuthorizedAsync(
                _agentId,
                _definition.ResourceType,
                _definition.ResourceId,
                "execute",
                _user,
                cancellationToken).ConfigureAwait(false);
            if (_definition.ParentResourceId != null)
            {
                await _authorization.EnsureAuthorizedAsync(
                    _agentId,
                    _definition.ResourceType,
                    _definition.ParentResourceId,
                    "execute",
                    _user,
                    cancellationToken).ConfigureAwait(false);
            }
            await _authorization.EnsureAuthorizedAsync(
                _agentId,
                AgentResourceType.Tool,
                _definition.Name,
                "execute",
                _user,
                cancellationToken).ConfigureAwait(false);
            await _authorization.EnsureAuthorizedAsync(
                _agentId,
                AgentResourceType.Function,
                _definition.Name,
                "execute",
                _user,
                cancellationToken).ConfigureAwait(false);
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
