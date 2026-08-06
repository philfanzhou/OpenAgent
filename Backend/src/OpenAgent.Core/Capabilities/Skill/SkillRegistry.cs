using System.Collections.Concurrent;
using OpenAgent.Core.Abstract;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, SkillEntry> _skills =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterTool(
        SkillDescriptor tool,
        Func<Dictionary<string, object>, CancellationToken, Task<string>> executor)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(executor);
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("Skill name cannot be empty.", nameof(tool));
        }

        _skills[tool.Name] = new SkillEntry(tool, executor);
    }

    public IReadOnlyList<SkillDescriptor> GetTools() =>
        _skills.Values.Select(entry => entry.Descriptor).ToList().AsReadOnly();

    public async Task<string> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_skills.TryGetValue(toolName, out SkillEntry? entry))
        {
            throw new KeyNotFoundException($"Skill '{toolName}' is not registered.");
        }

        return await entry.Executor(arguments, cancellationToken).ConfigureAwait(false);
    }

    public bool HasTool(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _skills.ContainsKey(toolName);

    private sealed record SkillEntry(
        SkillDescriptor Descriptor,
        Func<Dictionary<string, object>, CancellationToken, Task<string>> Executor);
}
