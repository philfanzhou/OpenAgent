using System.Diagnostics;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Runtime;

namespace OpenAgent.Application.Observability;

internal sealed class ToolAuditTrail(TurnContext turn, string? username) : IDisposable
{
    internal const string SourceName = "OpenAgent.Engine";
    private static readonly ActivitySource Source = new(SourceName);
    private readonly Dictionary<string, Activity> _pending = new(StringComparer.Ordinal);
    private const int MaxContentChars = 4000;

    internal void Observe(AgentStreamEvent item)
    {
        if (string.IsNullOrEmpty(item.ToolCallId))
            return;

        if (item.IsBridgedTool)
        {
            if (_pending.Remove(item.ToolCallId, out Activity? redundant))
            {
                // A late ACP name can identify a bridge call after the initial update.
                redundant.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
                redundant.Stop();
            }
            return;
        }
        if (item.Type == AgentStreamEventType.ToolCall)
            StartOrUpdate(item.ToolCallId, item);
        else if (item.Type == AgentStreamEventType.ToolResult && _pending.Remove(item.ToolCallId, out Activity? activity))
        {
            activity.SetTag("langfuse.observation.output", Truncate(item.Content));
            activity.SetTag("openagent.tool.status", item.ToolFailed ? "failed" : "completed");
            activity.Stop();
        }
    }

    private void StartOrUpdate(string callId, AgentStreamEvent item)
    {
        string name = item.ToolName ?? "tool";
        if (!_pending.TryGetValue(callId, out Activity? activity))
        {
            if (item.IsToolUpdate)
                return;
            Activity? parent = Activity.Current;
            activity = Source.StartActivity(name, ActivityKind.Internal);
            if (activity is null)
                return;
            Activity.Current = parent;
            _pending.Add(callId, activity);
            foreach (var tag in turn.ToAuditAttributes())
                activity.SetTag(tag.Key, tag.Value);
            activity.SetTag("openagent.username", username ?? string.Empty);
            activity.SetTag("openagent.tool.call_id", callId);
            activity.SetTag("langfuse.observation.type", "tool");
        }

        activity.DisplayName = name;
        activity.SetTag("openagent.tool.name", name);
        activity.SetTag("langfuse.observation.input", Truncate(item.ToolArguments?.ToString() ?? item.Content));
    }

    public void Dispose()
    {
        foreach (Activity activity in _pending.Values)
        {
            activity.SetTag("openagent.tool.status", "aborted");
            activity.Stop();
        }
        _pending.Clear();
    }

    private static string Truncate(string? value) => value is null || value.Length <= MaxContentChars
        ? value ?? string.Empty
        : value[..MaxContentChars] + "…[审计截断]";
}
