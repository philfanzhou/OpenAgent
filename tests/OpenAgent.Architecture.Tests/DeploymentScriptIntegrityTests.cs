using System.Text.RegularExpressions;
using Xunit;

namespace OpenAgent.Architecture.Tests;

/// <summary>
/// Deployment-script integrity.

/// The repository was split out of a monorepo: <c>Backend/</c> became the root
/// and <c>Frontend/OpenAgent.Chat</c> moved to the separate OpenAgent-Frontend
/// repository. The build/deploy scripts kept the old paths for a while, so
/// <c>scripts/build-images.sh</c> failed on the very first <c>docker build</c>
/// with "unable to prepare context: path .../Backend/src/... not found".
///
/// These tests pin the script's inputs to what actually exists in the checkout,
/// and pin LF line endings on everything a Linux process executes.
/// </summary>
public class DeploymentScriptIntegrityTests
{
    private static string RepoRoot { get; } = FindRepoRoot();

    [Fact]
    public void BuildScript_ReferencesOnlyExistingDockerfiles()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "build-images.sh"));

        string[] referenced = Regex
            .Matches(script, @"docker_root/([A-Za-z0-9._/-]+)/Dockerfile")
            .Select(m => m.Groups[1].Value + "/Dockerfile")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(referenced);

        // The chat workbench is a separate repository; the script must guard it
        // rather than assume it exists.
        string[] required = referenced
            .Where(path => !path.StartsWith("Frontend/", StringComparison.Ordinal))
            .ToArray();

        string[] missing = required
            .Where(path => !File.Exists(Path.Combine(RepoRoot, path)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "build-images.sh references Dockerfiles that do not exist: " + string.Join(", ", missing));

        if (referenced.Any(p => p.StartsWith("Frontend/", StringComparison.Ordinal)))
        {
            // The chat image must be optional: guarded by an existence check so
            // a backend-only deployment does not fail on a missing directory.
            Assert.Contains("chat_dockerfile=\"$docker_root/Frontend/OpenAgent.Chat/Dockerfile\"", script, StringComparison.Ordinal);
            Assert.Contains("build_chat=0", script, StringComparison.Ordinal);
            Assert.Contains("--file \"$chat_dockerfile\"", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildScript_HasNoMonorepoBackendPaths()
    {
        foreach (string script in new[]
                 {
                     Path.Combine("scripts", "build-images.sh"),
                     Path.Combine("scripts", "build-images.ps1"),
                     Path.Combine("deploy", "openagent", "preview.compose.yml"),
                 })
        {
            string path = Path.Combine(RepoRoot, script);
            Assume.That(File.Exists(path), $"{script} missing");
            string text = File.ReadAllText(path);

            Assert.DoesNotContain("Backend/src/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Backend/tests/", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Backend/OpenAgent.sln", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CiWorkflow_UsesRepositoryRootPaths()
    {
        string ci = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));

        Assert.DoesNotContain("Backend/", ci, StringComparison.Ordinal);
        Assert.Contains("dotnet test OpenAgent.sln", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void CiWorkflow_GuardsTheFrontendJob()
    {
        // Frontend/OpenAgent.Chat is not in this repository; the job must be
        // gated by a detector rather than failing on a missing directory.
        string ci = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("frontend-paths:", ci, StringComparison.Ordinal);
        Assert.Contains("needs: frontend-paths", ci, StringComparison.Ordinal);
        Assert.Contains("has_frontend", ci, StringComparison.Ordinal);

        // hashFiles() is step-level only; at job level it breaks workflow
        // evaluation. Ignore comment lines, which may legitimately mention it.
        string[] activeLines = ci
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !line.TrimStart().StartsWith('#'))
            .ToArray();

        Assert.DoesNotContain(activeLines, line => line.Contains("hashFiles(", StringComparison.Ordinal));
    }

    [Fact]
    public void ShellScripts_UseLfLineEndings()
    {
        // A CRLF checkout makes `bash script.sh` fail with
        // "syntax error near unexpected token `$'{\r''" on any Linux host.
        string[] scripts = Directory
            .GetFiles(Path.Combine(RepoRoot, "scripts"), "*.sh")
            .Concat(new[]
            {
                Path.Combine(RepoRoot, "deploy", "init_db.sh"),
                Path.Combine(RepoRoot, "deploy", "90_checks.sh"),
            })
            .Where(File.Exists)
            .ToArray();

        Assert.NotEmpty(scripts);

        var offenders = new List<string>();
        foreach (string script in scripts)
        {
            byte[] bytes = File.ReadAllBytes(script);
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
            {
                offenders.Add(Path.GetFileName(script));
            }
        }

        Assert.True(offenders.Count == 0, "CRLF in shell scripts: " + string.Join(", ", offenders));
    }

    [Fact]
    public void GitAttributes_PinsLfForLinuxExecutedFiles()
    {
        string path = Path.Combine(RepoRoot, ".gitattributes");
        Assert.True(File.Exists(path), ".gitattributes is required to keep shell scripts LF");

        string text = File.ReadAllText(path);
        Assert.Contains("*.sh", text, StringComparison.Ordinal);
        Assert.Contains("eol=lf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployScript_DoesNotRebuildOrTouchVolumes()
    {
        // deploy.sh must be a pure container replacement: rebuilding or
        // recreating volumes during an update would destroy audit data.
        string script = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "deploy.sh"));

        Assert.Contains("up --detach --no-build", script, StringComparison.Ordinal);
        Assert.DoesNotContain("down -v", script, StringComparison.Ordinal);
        Assert.DoesNotContain("volume rm", script, StringComparison.Ordinal);
        Assert.DoesNotContain("volume prune", script, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "OpenAgent.sln")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        return root!;
    }
}

/// <summary>Minimal stand-in for xunit.v3's Assume, which is not referenced here.</summary>
internal static class Assume
{
    public static void That(bool condition, string message)
    {
        if (!condition)
        {
            Assert.Fail(message);
        }
    }
}