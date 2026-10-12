namespace OpenAgent.Core.Execution;

internal sealed class AcpHarness
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = [];
    public bool OpenCode { get; set; }
    public string ModelIdTemplate { get; set; } = "{provider}/{model}";
    public string PermissionPolicy { get; set; } = "deny";
}
