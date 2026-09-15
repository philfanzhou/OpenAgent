using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Execution;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Capabilities.Code;
using OpenAgent.Core.Files;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Capabilities.Skill;

/// <summary>
/// Bridges MAF's run_skill_script tool to the isolated Runner. Package scripts
/// never execute in the Engine process: they are mounted as sandbox inputs and
/// launched through a generated wrapper main.py, sharing the same Bubblewrap
/// isolation, budget, and artifact pipeline as execute_code.
/// </summary>
internal sealed class SkillScriptRunner(
    ICodeExecutor executor,
    IFileAssetService files,
    FileAssetExecutionContext fileContext,
    AgentAuthorizationGate authorization,
    CodeExecutionBudget budget,
    IOptions<CodeExecutionOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Relaxed escaping keeps the wrapper readable in Runner logs: quotes stay
    /// as \" (still a valid Python string-literal escape) instead of \u0022.
    /// </summary>
    private static readonly JsonSerializerOptions LiteralOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal Task<object?> RunAsync(
        string agentId,
        IAgentUserContext user,
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        CancellationToken cancellationToken) =>
        RunAsync(agentId, user, skill.Frontmatter.Name, script.FullPath, arguments, cancellationToken);

    internal async Task<object?> RunAsync(
        string agentId,
        IAgentUserContext user,
        string skillName,
        string scriptFullPath,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || fileContext.Scope is not { } scope)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "Skill script execution is not enabled.");
        }
        // Recheck authorization at invocation, including calls after a long model turn.
        foreach ((AgentResourceType type, string id) in new[]
        {
            (AgentResourceType.Tool, "code-execution"),
            (AgentResourceType.Tool, "run_skill_script"),
            (AgentResourceType.Function, "run_skill_script"),
            (AgentResourceType.Skill, skillName)
        })
        {
            if (!await authorization.IsAvailableAsync(agentId, type, id, user, cancellationToken).ConfigureAwait(false))
            {
                throw new AgentException(AgentErrorCode.PermissionDenied, "Skill script execution is not authorized.");
            }
        }
        try
        {
            if (!budget.TryConsume(options.Value.MaxExecutionsPerRequest))
            {
                return "{\"error\":\"Code execution budget exhausted for this request.\"}";
            }
            CodeExecutionRequest request = await BuildRequestAsync(scriptFullPath, arguments, cancellationToken).ConfigureAwait(false);
            CodeExecutionResult result = await executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            ExecutionLimits.ValidateFiles(result.Files);
            List<object> artifacts = await CodeExecutionArtifacts.PublishAsync(
                result, files, scope, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new
            {
                result.ExecutionId, result.ExitCode, result.TimedOut, result.Stdout, result.Stderr, files = artifacts
            }, JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or AgentException)
        {
            return JsonSerializer.Serialize(new { error = exception.Message }, JsonOptions);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return "{\"error\":\"The isolated Runner is unavailable or returned an invalid result. No host execution fallback is permitted.\"}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "{\"error\":\"The Runner request timed out.\"}";
        }
    }

    /// <summary>
    /// Mounts the target script and its sibling files from the materialized
    /// package directory as flat sandbox inputs. Names are validated by
    /// <see cref="ExecutionLimits"/>, which also enforces the file count and
    /// size limits and the reserved main.py name.
    /// </summary>
    private static async Task<CodeExecutionRequest> BuildRequestAsync(
        string scriptFullPath,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        string scriptName = Path.GetFileName(scriptFullPath);
        string? scriptDirectory = Path.GetDirectoryName(scriptFullPath);
        if (string.IsNullOrWhiteSpace(scriptDirectory)
            || !File.Exists(scriptFullPath)
            || !Directory.Exists(scriptDirectory))
        {
            throw new ArgumentException("Skill script is no longer available.");
        }
        var request = new CodeExecutionRequest
        {
            Code = BuildWrapperCode(scriptName, arguments)
        };
        foreach (string path in Directory.EnumerateFiles(scriptDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            request.Files.Add(new ExecutionFile
            {
                Name = Path.GetFileName(path),
                Content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            });
        }
        ExecutionLimits.Validate(request);
        return request;
    }

    /// <summary>
    /// Generates the wrapper main.py: the working directory becomes /input and
    /// the target script runs as __main__ via runpy. A JSON object or array of
    /// arguments is surfaced to the script as sys.argv (string values are
    /// passed through, others are JSON-encoded). JSON escaping produces a
    /// Python-safe string literal, and script names are restricted to
    /// letter-or-digit characters with a safe punctuation set, so neither can
    /// inject code into the wrapper.
    /// </summary>
    private static string BuildWrapperCode(string scriptName, JsonElement? arguments)
    {
        string scriptLiteral = JsonSerializer.Serialize(scriptName, LiteralOptions);
        string rawAssignment = arguments.HasValue
            ? $"raw = json.loads({JsonSerializer.Serialize(arguments.Value.GetRawText(), LiteralOptions)})"
            : "raw = None";
        return $"""
            import json, os, runpy, sys
            os.chdir("/input")
            {rawAssignment}
            extra = []
            if isinstance(raw, dict):
                extra = [value if isinstance(value, str) else json.dumps(value) for value in raw.values()]
            elif isinstance(raw, list):
                extra = [value if isinstance(value, str) else json.dumps(value) for value in raw]
            elif raw is not None:
                extra = [json.dumps(raw)]
            sys.argv = [{scriptLiteral}, *extra]
            runpy.run_path("/input/{scriptName}", run_name="__main__")
            """;
    }
}
