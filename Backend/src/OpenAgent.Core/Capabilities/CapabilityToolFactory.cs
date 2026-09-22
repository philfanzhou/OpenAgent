using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities;

internal sealed class CapabilityToolFactory
{
    private readonly IReadOnlyList<ICapabilitySource> _sources;
    private readonly AgentAuthorizationGate _authorization;
    private readonly ILogger<CapabilityToolFactory> _logger;
    private readonly IHostEnvironment? _environment;

    public CapabilityToolFactory(
        IEnumerable<ICapabilitySource> sources,
        AgentAuthorizationGate authorization,
        ILogger<CapabilityToolFactory> logger,
        IHostEnvironment? environment = null)
    {
        _sources = sources.ToList().AsReadOnly();
        _authorization = authorization;
        _logger = logger;
        _environment = environment;
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
                // 每代理工具裁剪：禁用项不进入模型可见集合（ACL 语义不变）。
                if (ToolSelection.IsDisabled(config.Tools.Disabled, definition.Name))
                {
                    continue;
                }
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
                    tools.Add(new CapabilityAIFunction(definition, _logger, _environment));
                }
            }
        }

        return tools.AsReadOnly();
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

    private sealed class CapabilityAIFunction : AIFunction, IToolConcurrencyProvider
    {
        private readonly CapabilityDefinition _definition;
        private readonly JsonElement _schema;
        private readonly ILogger<CapabilityToolFactory> _logger;
        private readonly IHostEnvironment? _environment;

        internal CapabilityAIFunction(
            CapabilityDefinition definition,
            ILogger<CapabilityToolFactory> logger,
            IHostEnvironment? environment)
        {
            _definition = definition;
            _logger = logger;
            _environment = environment;
            using JsonDocument schema = JsonDocument.Parse(NormalizeSchema(definition.ParametersJsonSchema));
            _schema = schema.RootElement.Clone();
        }

        public override string Name => _definition.Name;
        public override string Description => _definition.Description;
        public override JsonElement JsonSchema => _schema;
        public ToolConcurrency Concurrency => _definition.Concurrency;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, object?> values = arguments.ToDictionary(
                item => item.Key,
                item => item.Value);
            // 错误信封已在 ToolResult.Error 构造时渲染成 Content；此处直接落成
            // 字符串，保证任何调用路径（含未包 IsolatedToolFunction 的测试/诊断
            // 路径）拿到的都是模型可读文本而不是 record 序列化。
            ToolResult result = await _definition.Invoke(values, cancellationToken).ConfigureAwait(false);
            return result.Content;
        }

        private string NormalizeSchema(string? schema)
        {
            if (string.IsNullOrWhiteSpace(schema))
            {
                _logger.LogWarning(
                    "Capability {CapabilityName} has no parameter schema; falling back to {{\"type\":\"object\"}}",
                    _definition.Name);
                return "{\"type\":\"object\"}";
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(schema);
                return document.RootElement.GetRawText();
            }
            catch (JsonException exception)
            {
                // 非法 schema 是开发期缺陷：生产环境降级为无参工具并告警，
                // 开发环境直接失败，避免带病上线。
                _logger.LogError(
                    exception,
                    "Capability {CapabilityName} has an invalid parameter schema; falling back to {{\"type\":\"object\"}}",
                    _definition.Name);
                if (_environment?.IsDevelopment() == true)
                {
                    throw new InvalidOperationException(
                        $"Capability '{_definition.Name}' has an invalid ParametersJsonSchema.", exception);
                }
                return "{\"type\":\"object\"}";
            }
        }
    }
}
