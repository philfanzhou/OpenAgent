namespace OpenAgent.Framework.Entities;

internal sealed class McpConfigurationEntity
{
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";
}
