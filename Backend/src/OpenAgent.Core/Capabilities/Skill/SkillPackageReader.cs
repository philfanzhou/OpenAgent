using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenAgent.Contracts.Skills;

namespace OpenAgent.Core.Capabilities.Skill;

internal sealed class SkillPackageReader : ISkillPackageReader
{
    private const int MaxManifestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SkillPackageManifest Read(string fileName, ReadOnlyMemory<byte> content)
    {
        string format = GetFormat(fileName);
        SkillPackageManifest manifest = format switch
        {
            "json" => ReadJson(content.Span),
            "yaml" => ReadYaml(DecodeText(content.Span)),
            "markdown" => ReadMarkdown(DecodeText(content.Span)),
            "zip" => ReadZip(content),
            _ => throw new InvalidOperationException($"Skill package format '{format}' is not supported.")
        };

        Validate(manifest);
        return manifest;
    }

    public string GetFormat(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".json" => "json",
        ".yaml" or ".yml" => "yaml",
        ".md" or ".markdown" => "markdown",
        ".zip" => "zip",
        _ => throw new InvalidOperationException(
            "Skill packages must be JSON, YAML, Markdown, or ZIP files.")
    };

    private static SkillPackageManifest ReadJson(ReadOnlySpan<byte> content) =>
        JsonSerializer.Deserialize<SkillPackageManifest>(content, JsonOptions)
        ?? throw new InvalidOperationException("Skill JSON manifest is empty.");

    private static string DecodeText(ReadOnlySpan<byte> content) =>
        Encoding.UTF8.GetString(content).TrimStart('\uFEFF');

    private static SkillPackageManifest ReadYaml(string content)
    {
        Dictionary<string, string> values = ParseYaml(content);
        return FromValues(values);
    }

    private static SkillPackageManifest ReadMarkdown(string content)
    {
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Markdown skill packages require YAML front matter.");
        }

        int end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException("Markdown skill front matter is not terminated.");
        }

        Dictionary<string, string> values = ParseYaml(normalized[4..end]);
        if (!values.ContainsKey("description"))
        {
            values["description"] = normalized[(end + 5)..].Trim();
        }

        return FromValues(values);
    }

    private SkillPackageManifest ReadZip(ReadOnlyMemory<byte> content)
    {
        using var input = new MemoryStream(content.ToArray(), writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        ZipArchiveEntry? manifest = archive.Entries.FirstOrDefault(entry =>
            string.Equals(Path.GetFileName(entry.FullName), "skill.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(entry.FullName), "skill.yaml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(entry.FullName), "skill.yml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(entry.FullName), "SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (manifest == null)
        {
            throw new InvalidOperationException(
                "ZIP skill packages require skill.json, skill.yaml, skill.yml, or SKILL.md.");
        }

        if (manifest.Length > MaxManifestBytes)
        {
            throw new InvalidOperationException("Skill manifest exceeds the 1 MB limit.");
        }

        using Stream stream = manifest.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(manifest.Name, buffer.ToArray());
    }

    private static Dictionary<string, string> ParseYaml(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            values[key] = Unquote(value);
        }

        return values;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static SkillPackageManifest FromValues(IReadOnlyDictionary<string, string> values) => new()
    {
        Id = Get(values, "id", "skillId"),
        Name = Get(values, "name"),
        Description = Get(values, "description"),
        Version = Get(values, "version"),
        Type = Get(values, "type") is { Length: > 0 } type ? type : "HttpEndpoint",
        EndpointUrl = Get(values, "endpointUrl", "endpoint"),
        ParametersJsonSchema = Get(values, "parametersJsonSchema", "inputSchema") is { Length: > 0 } schema
            ? schema
            : "{\"type\":\"object\"}"
    };

    private static string Get(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (values.TryGetValue(key, out string? value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static void Validate(SkillPackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new InvalidOperationException("Skill manifests require both id and name.");
        }

        if (!string.Equals(manifest.Type, "HttpEndpoint", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Skill type '{manifest.Type}' is not supported. Only HttpEndpoint is supported.");
        }

        if (!Uri.TryCreate(manifest.EndpointUrl, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("HTTP endpoint skills require an absolute HTTP or HTTPS endpointUrl.");
        }

        try
        {
            using JsonDocument schema = JsonDocument.Parse(manifest.ParametersJsonSchema);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("parametersJsonSchema must contain valid JSON.", exception);
        }
    }
}
