namespace OpenAgent.Framework.Entities;

internal sealed class RuntimeConfigurationEntity
{
    public string TenantId { get; set; } = string.Empty;
    public string RuntimeId { get; set; } = string.Empty;
    public string DefinitionJson { get; set; } = "{}";
}
