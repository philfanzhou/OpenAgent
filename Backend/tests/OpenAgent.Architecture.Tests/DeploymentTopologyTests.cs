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

    private static string EnvExamplePath =>
        Path.Combine(RepoRoot, "deploy", "deployment.env.example");

    [Fact]
    public void Compose_ForwardsPerSignalOtlpSettingsToEngineAndRouter()
    {
        string compose = File.ReadAllText(ComposePath);

        // Both services export spans; each needs the per-signal endpoint and headers.
        Assert.Equal(2, CountOccurrences(compose, "OpenTelemetry__TracesEndpoint"));
        Assert.Equal(2, CountOccurrences(compose, "OpenTelemetry__TracesHeaders"));
        Assert.Equal(2, CountOccurrences(compose, "OpenTelemetry__Protocol"));
    }

    [Fact]
    public void Compose_KeepsTheSharedEndpointAsAFallback()
    {
        // The per-signal variables must add to the shared one, not replace it:
        // deployments that only set OPENAGENT_OTLP_ENDPOINT keep working.
        string compose = File.ReadAllText(ComposePath);

        Assert.Contains("OpenTelemetry__OtlpEndpoint", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvExample_DoesNotReferenceVariablesInsideValues()
    {
        // Docker Compose does not expand ${...} inside .env values. A header
        // written as "Authorization=Bearer ${AUDIT_GATEWAY_TOKEN}" reaches the
        // container literally, the gateway answers 401 for every batch, and all
        // audit data is dropped while both services look healthy.
        string example = File.ReadAllText(EnvExamplePath);

        string[] offenders = example
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0 && !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains('='))
            .Where(line =>
            {
                int separator = line.IndexOf('=');
                string value = line[(separator + 1)..];
                return Regex.IsMatch(value, @"\$\{[A-Za-z_][A-Za-z0-9_]*\}");
            })
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Values must not embed ${VAR}; compose will not expand it: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Compose_DeclaresTheSharedNetworkByName()
    {
        // Declaring the network external on both sides deadlocks a first deploy:
        // each stack waits for the other to create it. Declaring it by name lets
        // either stack create it and the other reuse it.
        string compose = File.ReadAllText(ComposePath);
        Match network = Regex.Match(compose, @"^\s{2}openagent:\s*\n\s+name:\s*\S+", RegexOptions.Multiline);

        Assert.True(network.Success, "the openagent network must be declared with an explicit name");
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
            .Matches(script, @"docker_root/([A-Za-z0-9._/-]+)/Dockerfile")
            .Select(match => match.Groups[1].Value + "/Dockerfile")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referenced);

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