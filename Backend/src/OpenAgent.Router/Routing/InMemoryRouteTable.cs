namespace OpenAgent.Router;

public class InMemoryRouteTable : IRouteTable
{
    private readonly IConfiguration _config;

    public InMemoryRouteTable(IConfiguration config)
    {
        _config = config;
    }

    public string? GetTargetEndpoint(string intent)
    {
        var settings = _config.GetSection("RouterSettings:Routing");
        return intent switch
        {
            "workflow" => settings["WorkflowEndpoint"] ?? "http://localhost:5003",
            _ => settings["EngineEndpoint"] ?? "http://localhost:5208"
        };
    }

    public string? GetTargetEndpoint(
        string intent,
        string? capability,
        string? tenantId,
        string? conversationId)
    {
        return GetTargetEndpoint(intent);
    }
}
