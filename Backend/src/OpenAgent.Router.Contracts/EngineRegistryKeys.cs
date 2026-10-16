namespace OpenAgent.Router;

/// <summary>
/// Redis keys of the Engine registry. Engine writes them and Router reads them, so both
/// sides use this single definition.
/// </summary>
public static class EngineRegistryKeys
{
    /// <summary>Set of registered engine ids.</summary>
    public const string Index = "engine:registry:index";

    /// <summary>String holding the JSON-serialized <see cref="EngineRegistryEntry"/> of one engine.</summary>
    public static string ForEngine(string engineId) => $"engine:registry:{engineId}";
}
