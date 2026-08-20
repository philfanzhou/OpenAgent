using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenAgent.Contracts.Requests;

namespace OpenAgent.Core.Runtime.Agent;

internal static class EngineMeter
{
    internal const string MeterName = "OpenAgent.Engine";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> AgentCallsTotal = Meter.CreateCounter<long>(
        "openagent_engine_agent_calls_total");
    private static readonly Counter<long> ExecutionsTotal = Meter.CreateCounter<long>(
        "openagent_engine_executions_total");
    private static readonly Counter<long> CapabilityCallsTotal = Meter.CreateCounter<long>(
        "openagent_engine_capability_calls_total");
    private static readonly Counter<long> TokensTotal = Meter.CreateCounter<long>(
        "openagent_engine_tokens_total");
    private static readonly Counter<long> CompressionsTotal = Meter.CreateCounter<long>(
        "openagent_engine_compressions_total");

    internal static EngineExecutionMeasurement StartAgentCall(string mode)
    {
        string normalizedMode = NormalizeMode(mode);
        AgentCallsTotal.Add(1, new TagList { { "mode", normalizedMode } });
        return new EngineExecutionMeasurement(normalizedMode);
    }

    internal static void RecordCapabilityCall(string? name)
    {
        CapabilityCallsTotal.Add(1, new TagList
        {
            { "kind", IsSkillCall(name) ? "skill" : "tool" }
        });
    }

    internal static void RecordCompression(string strategy)
    {
        CompressionsTotal.Add(1, new TagList
        {
            { "strategy", NormalizeCompressionStrategy(strategy) }
        });
    }

    private static void RecordExecution(string mode, bool succeeded, TokenUsage? usage)
    {
        ExecutionsTotal.Add(1, new TagList
        {
            { "mode", mode },
            { "outcome", succeeded ? "success" : "failure" }
        });
        if (succeeded && usage != null)
        {
            RecordTokenUsage(mode, usage);
        }
    }

    private static void RecordTokenUsage(string mode, TokenUsage usage)
    {
        RecordTokens(mode, "input", usage.PromptTokens);
        RecordTokens(mode, "output", usage.CompletionTokens);
        RecordTokens(mode, "total", usage.TotalTokens);
        RecordTokens(mode, "cached_input", usage.CachedInputTokens);
        RecordTokens(mode, "reasoning", usage.ReasoningTokens);
    }

    private static void RecordTokens(string mode, string type, int? value)
    {
        if (value is not > 0)
        {
            return;
        }

        TokensTotal.Add(value.Value, new TagList
        {
            { "mode", mode },
            { "type", type }
        });
    }

    private static bool IsSkillCall(string? name) => name?.Trim().ToLowerInvariant() is
        "load_skill" or "read_skill_resource" or "run_skill_script";

    private static string NormalizeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "stream" => "stream",
        _ => "sync"
    };

    private static string NormalizeCompressionStrategy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "summarize" => "summarize",
        "sliding_window" => "sliding_window",
        "truncation" => "truncation",
        _ => "other"
    };

    internal sealed class EngineExecutionMeasurement(string mode) : IDisposable
    {
        private bool _completed;
        private TokenUsage? _usage;

        internal void Complete(TokenUsage? usage)
        {
            _completed = true;
            _usage = usage;
        }

        public void Dispose() => RecordExecution(mode, _completed, _usage);
    }
}
