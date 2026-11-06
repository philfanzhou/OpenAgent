using System.Text.RegularExpressions;
using Xunit;

namespace OpenAgent.Architecture.Tests;

/// <summary>
/// Guards the deployment topology that the OpenAgent.Audit integration relies
/// on. These are text assertions on the compose file rather than a YAML
/// round-trip: the repository takes no YAML dependency, and the invariants are
/// about how specific keys are declared.
/// </summary>
public class DeploymentTopologyTests
{
    private static string ComposeFile { get; } = ReadComposeFile();

    [Fact]
    public void AuditNetwork_IsDeclaredByName_NotExternal()
    {
        // Both stacks declare `openagent-audit` by name. Docker reuses an
        // existing network of the same name (warning only), so either stack may
        // start first. Declaring it external on both sides deadlocks with
        // "network openagent-audit declared as external, but could not be found".
        string auditNetworkBlock = ExtractNetworkBlock("audit");

        Assert.DoesNotContain("external", auditNetworkBlock, StringComparison.Ordinal);
        Assert.Contains("${OPENAGENT_AUDIT_NETWORK:-openagent-audit}", auditNetworkBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyEngineAndRouter_JoinTheAuditNetwork()
    {
        // Runner, chat and nginx have no business reaching the audit plane; the
        // engine is where traces originate and the router forwards to it.
        foreach (string service in new[] { "engine", "router" })
        {
            string block = ExtractServiceBlock(service);
            Assert.Contains("- audit", block, StringComparison.Ordinal);
        }

        foreach (string service in new[] { "runner", "chat", "nginx" })
        {
            string block = ExtractServiceBlock(service);
            Assert.DoesNotContain("- audit", block, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AuditTraces_RouteOnlyTracesToTheGateway()
    {
        // Logs and metrics must keep the shared collector endpoint: log lines
        // carry request text the masking pipeline never sees, so sending them to
        // the audit gateway would breach its boundary.
        Assert.Contains(
            "OpenTelemetry__TracesEndpoint: ${OPENAGENT_OTLP_TRACES_ENDPOINT:-}",
            ComposeFile,
            StringComparison.Ordinal);
        Assert.Contains(
            "OpenTelemetry__OtlpEndpoint: ${OPENAGENT_OTLP_ENDPOINT:-}",
            ComposeFile,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuditTracesHeaders_AreForwardedByBothExporters()
    {
        // The masking gateway rejects unauthenticated ingest (AUDIT_GATEWAY_REQUIRE_AUTH=true),
        // so a missing header means traces are silently dropped at the gateway with a 401.
        int occurrences = Regex.Matches(
            ComposeFile,
            "OpenTelemetry__TracesHeaders: \\$\\{OPENAGENT_OTLP_TRACES_HEADERS:-}").Count;

        Assert.Equal(2, occurrences); // engine + router
    }

    [Fact]
    public void DeploymentEnvExample_DoesNotReferenceVariablesInsideValues()
    {
        // Compose does NOT expand variables inside .env values; it passes them
        // through verbatim. Writing "Authorization=Bearer ${AUDIT_GATEWAY_TOKEN}"
        // ships that literal text as the bearer credential, and the gateway
        // answers 401 for every batch — an outage that looks like a network
        // problem. Verified with `docker run --env-file`.
        string envExample = ReadDeploymentEnvExample();

        string[] offenders = envExample
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Where(line => line.Contains('='))
            .Where(line => line[(line.IndexOf('=') + 1)..].Contains("${", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "env values are literal; these reference variables that will NOT be expanded: "
                + string.Join(" | ", offenders));
    }

    private static string ReadDeploymentEnvExample()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "OpenAgent.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        string path = Path.Combine(root!, "deploy", "deployment.env.example");
        Assert.True(File.Exists(path), $"Missing {path}");
        return File.ReadAllText(path);
    }

    private static string ExtractNetworkBlock(string name)
    {
        Match match = Regex.Match(
            ComposeFile,
            $@"(?m)^  {Regex.Escape(name)}:\r?\n((?:^    .*\r?\n)+)");
        Assert.True(match.Success, $"network '{name}' not found in docker-compose.yml");
        return match.Groups[1].Value;
    }

    private static string ExtractServiceBlock(string name)
    {
        Match match = Regex.Match(
            ComposeFile,
            $@"(?ms)^  {Regex.Escape(name)}:\r?\n(.*?)(?=^  \w[\w-]*:\r?$|^networks:)");
        Assert.True(match.Success, $"service '{name}' not found in docker-compose.yml");
        return match.Groups[1].Value;
    }

    private static string ReadComposeFile()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "OpenAgent.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        string path = Path.Combine(root!, "deploy", "openagent", "docker-compose.yml");
        Assert.True(File.Exists(path), $"Missing {path}");
        return File.ReadAllText(path);
    }
}