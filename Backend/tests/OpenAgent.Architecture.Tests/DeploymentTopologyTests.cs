using System.Text.RegularExpressions;
using Xunit;

namespace OpenAgent.Architecture.Tests;

/// <summary>
/// Deployment configuration invariants for the audit plane.
/// </summary>
/// <remarks>
/// These are checked here rather than in a deployment script because the
/// failure mode is silent: OpenAgent starts, reports healthy, and simply never
/// delivers audit data. A wrong header value or a dropped endpoint is invisible
/// until somebody goes looking for a conversation that was never recorded.
/// </remarks>
public class DeploymentTopologyTests
{
    private static string RepoRoot => FindRepoRoot();

    private static string ComposePath =>
        Path.Combine(RepoRoot, "deploy", "openagent", "docker-compose.yml");

    private static string SharedEnvExamplePath =>
        Path.Combine(RepoRoot, "deploy", "openagent", ".env.example");

    [Fact]
    public void Compose_SharesAuditOtlpSettingsBetweenEngineAndRouter()
    {
        string compose = File.ReadAllText(ComposePath);
        string shared = File.ReadAllText(SharedEnvExamplePath);

        // Both services load the same runtime env_file. Optional audit values
        // live there once rather than being duplicated in Compose environment.
        Assert.Equal(2, Regex.Matches(compose, @"(?m)^      - \./\.env$").Count);
        Assert.Equal(1, CountOccurrences(shared, "OTEL_EXPORTER_OTLP_ENDPOINT="));
        Assert.Equal(1, CountOccurrences(shared, "OTEL_EXPORTER_OTLP_HEADERS="));
        Assert.Equal(1, CountOccurrences(shared, "OTEL_EXPORTER_OTLP_PROTOCOL="));
        Assert.DoesNotContain("OTEL_TRACES_EXPORTER=", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEnv_KeepsTheSharedEndpointAsAFallback()
    {
        string shared = File.ReadAllText(SharedEnvExamplePath);

        Assert.Contains("OpenTelemetry__OtlpEndpoint=", shared, StringComparison.Ordinal);
        Assert.Contains("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=", shared, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openagent")]
    [InlineData("openagent/engine")]
    [InlineData("openagent/router")]
    [InlineData("nginx")]
    public void ServiceEnvExamples_UseLiteralValues(string directory)
    {
        // Service configuration is entered directly, without a separate top-level alias file.
        string example = File.ReadAllText(Path.Combine(RepoRoot, "deploy", directory, ".env.example"));

        string[] offenders = example
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains('='))
            .Where(line =>
            {
                int separator = line.IndexOf('=');
                string value = line[(separator + 1)..];
                return value.Contains("${", StringComparison.Ordinal);
            })
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Service environment values must be literals: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Compose_NginxOwnsTheFixedSharedNetwork()
    {
        string app = File.ReadAllText(ComposePath);
        string nginx = File.ReadAllText(Path.Combine(RepoRoot, "deploy", "nginx", "docker-compose.yml"));
        const string networkPattern = @"(?m)^  openagent:\r?\n(?:    .*\r?\n)+";
        Match appNetwork = Regex.Match(app, networkPattern);
        Match nginxNetwork = Regex.Match(nginx, networkPattern);

        Assert.True(appNetwork.Success);
        Assert.True(nginxNetwork.Success);
        Assert.Contains("name: openagent", appNetwork.Value, StringComparison.Ordinal);
        Assert.Contains("name: openagent", nginxNetwork.Value, StringComparison.Ordinal);
        Assert.Contains("external: true", appNetwork.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("external:", nginxNetwork.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellScripts_UseLfLineEndings()
    {
        // A CRLF checkout fails on the first line under Linux with
        // "syntax error near unexpected token `$'{\r''", so the deploy scripts
        // could never run on the server.
        string scriptsDirectory = Path.Combine(RepoRoot, "scripts");
        string[] scripts = Directory.GetFiles(scriptsDirectory, "*.sh");

        Assert.NotEmpty(scripts);

        string[] offenders = scripts
            .Where(script => Array.IndexOf(File.ReadAllBytes(script), (byte)'\r') >= 0)
            .Select(Path.GetFileName)
            .ToArray()!;

        Assert.True(offenders.Length == 0, "CRLF in shell scripts: " + string.Join(", ", offenders));
    }

    [Fact]
    public void BuildScript_ReferencesExistingDockerfiles()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "build-images.sh"));

        string[] referenced = Regex
            .Matches(script, @"\bdockerfile=([A-Za-z0-9._/-]+/Dockerfile)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(3, referenced.Length);
        Assert.Contains("Backend/opencode.version", script, StringComparison.Ordinal);
        Assert.Contains("OPENCODE_VERSION=$opencode_version", script, StringComparison.Ordinal);

        string[] missing = referenced
            .Where(path => !File.Exists(Path.Combine(RepoRoot, path)))
            .ToArray();

        Assert.True(missing.Length == 0, "build-images.sh references missing Dockerfiles: " + string.Join(", ", missing));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "Backend")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        return root!;
    }
}
