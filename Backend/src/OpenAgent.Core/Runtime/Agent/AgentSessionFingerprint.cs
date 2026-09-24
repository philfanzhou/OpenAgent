using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Core.Runtime.Agent;

internal static class AgentSessionFingerprint
{
    internal static string Create(AgentRuntimeProfile profile)
    {
        string descriptor = JsonSerializer.Serialize(new
        {
            profile.AgentId,
            Config = new
            {
                profile.Config.Instructions,
                profile.Config.MaxTurns,
                McpServers = profile.Config.Mcp.EnabledServerIds
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                RagInstances = profile.Config.Rag.EnabledRagInstanceIds
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                RagEnabled = profile.Config.Rag.Enabled,
                Skills = profile.Config.Skills.EnabledSkills
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                SkillDigests = profile.Config.Skills.Instances
                    .Where(skill => profile.Config.Skills.EnabledSkills.Contains(skill.Id, StringComparer.Ordinal))
                    .Select(skill => new { skill.Id, skill.Sha256, skill.ScriptExecutionEnabled })
                    .OrderBy(skill => skill.Id, StringComparer.Ordinal)
                    .ToArray(),
                CodeExecutionEnabled = profile.Config.CodeExecution.Enabled,
                profile.Config.ContextPolicy
            },
            Model = new
            {
                profile.Model.Provider,
                profile.Model.Format,
                profile.Model.ModelId,
                profile.Model.Endpoint,
                profile.Model.Modality,
                profile.Model.ContextTokens
            }
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor)));
    }
}
