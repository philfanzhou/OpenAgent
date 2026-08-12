namespace OpenAgent.Contracts.Skills;

public sealed class SkillPackageManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Type { get; init; } = "HttpEndpoint";
    public string EndpointUrl { get; init; } = string.Empty;
    public string ParametersJsonSchema { get; init; } = "{\"type\":\"object\"}";
}

public interface ISkillPackageReader
{
    SkillPackageManifest Read(string fileName, ReadOnlyMemory<byte> content);

    string GetFormat(string fileName);
}
