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
/// never execute in the Engine process: the whole materialized package is
/// mounted as sandbox inputs (preserving its directory layout, so intra-package
/// imports resolve) and launched through a generated wrapper entry file —
/// Python via runpy, JavaScript via a dynamic import, shell via bash — sharing
/// the same Bubblewrap isolation and artifact pipeline as execute_code.
/// </summary>
internal sealed class SkillScriptRunner(
    ICodeExecutor executor,
    IFileAssetService files,
    FileAssetExecutionContext fileContext,
    AgentAuthorizationGate authorization,
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

    /// <summary>
    /// Skill packages may legitimately ship their own main.py / main.mjs /
    /// main.sh, so the wrappers use dedicated entry names per language instead
    /// of the language defaults.
    /// </summary>
    internal const string PythonWrapperEntry = "openagent_skill_entry__.py";
    internal const string JavaScriptWrapperEntry = "openagent_skill_entry__.mjs";
    internal const string ShellWrapperEntry = "openagent_skill_entry__.sh";

    internal Task<object?> RunAsync(
        string agentId,
        IAgentUserContext user,
        AgentFileSkill skill,
        AgentFileSkillScript script,
        JsonElement? arguments,
        CancellationToken cancellationToken) => RunAsync(
            agentId,
            user,
            skill.Frontmatter.Name,
            script.FullPath,
            ResolveSkillRoot(skill),
            arguments,
            cancellationToken);

    internal Task<object?> RunAsync(
        string agentId,
        IAgentUserContext user,
        string skillName,
        string scriptFullPath,
        JsonElement? arguments,
        CancellationToken cancellationToken) => RunAsync(
            agentId,
            user,
            skillName,
            scriptFullPath,
            FindSkillRoot(scriptFullPath),
            arguments,
            cancellationToken);

    private async Task<object?> RunAsync(
        string agentId,
        IAgentUserContext user,
        string skillName,
        string scriptFullPath,
        string skillRoot,
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
            CodeExecutionRequest request = await BuildRequestAsync(
                skillRoot, scriptFullPath, scope, arguments, cancellationToken).ConfigureAwait(false);
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

    /// <summary>MAF reports the skill's own directory path.</summary>
    private static string ResolveSkillRoot(AgentFileSkill skill) => skill.Path;

    /// <summary>Walks up from the script to the nearest directory holding SKILL.md.</summary>
    private static string FindSkillRoot(string scriptFullPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(scriptFullPath));
        for (int depth = 0; !string.IsNullOrWhiteSpace(directory) && depth < 8; depth++)
        {
            if (File.Exists(Path.Combine(directory, "SKILL.md")))
            {
                return directory;
            }
            directory = Path.GetDirectoryName(directory);
        }
        return Path.GetDirectoryName(Path.GetFullPath(scriptFullPath))
            ?? throw new ArgumentException("Skill script is no longer available.");
    }

    internal static string ResolveLanguage(string scriptPath) =>
        Path.GetExtension(scriptPath) switch
        {
            ".js" or ".mjs" => ExecutionLanguage.JavaScript,
            ".sh" => ExecutionLanguage.Shell,
            _ => ExecutionLanguage.Python
        };

    private static string WrapperEntry(string language) => language switch
    {
        ExecutionLanguage.JavaScript => JavaScriptWrapperEntry,
        ExecutionLanguage.Shell => ShellWrapperEntry,
        _ => PythonWrapperEntry
    };

    /// <summary>
    /// Mounts the entire skill package — not just the script's flat neighbours —
    /// with names relative to the package root, so scripts importing sibling
    /// modules or package data resolve inside the sandbox. Names are validated by
    /// <see cref="ExecutionLimits"/>, which also enforces the file count and size
    /// limits; the conversation-scoped session key keeps one workspace mounted
    /// across the runs of a single conversation.
    /// </summary>
    private static async Task<CodeExecutionRequest> BuildRequestAsync(
        string skillRoot,
        string scriptFullPath,
        FileAssetScope scope,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        string fullScriptPath = Path.GetFullPath(scriptFullPath);
        string fullRoot = Path.GetFullPath(skillRoot);
        string? scriptRelative = Path.GetRelativePath(fullRoot, fullScriptPath).Replace('\\', '/');
        if (!File.Exists(fullScriptPath)
            || !Directory.Exists(fullRoot)
            || scriptRelative.StartsWith("../", StringComparison.Ordinal)
            || !ExecutionLimits.IsSafeFileName(scriptRelative))
        {
            throw new ArgumentException("Skill script is no longer available.");
        }
        string language = ResolveLanguage(fullScriptPath);
        var request = new CodeExecutionRequest
        {
            Code = language switch
            {
                ExecutionLanguage.JavaScript => BuildJavaScriptWrapperCode(scriptRelative, arguments),
                ExecutionLanguage.Shell => BuildShellWrapperCode(scriptRelative, arguments),
                _ => BuildWrapperCode(scriptRelative, arguments)
            },
            Language = language,
            EntryFileName = WrapperEntry(language),
            // Reuse one persistent sandbox across the runs of a conversation so the
            // package survives; ids outside the safe charset run stateless.
            SessionKey = ExecutionLimits.IsSafeSessionKey(scope.ConversationId)
                ? scope.ConversationId
                : null
        };
        foreach (string path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(fullRoot, path).Replace('\\', '/');
            if (!ExecutionLimits.IsSafeFileName(relative))
            {
                continue;
            }
            request.Files.Add(new ExecutionFile
            {
                Name = relative,
                Content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)
            });
        }
        ExecutionLimits.Validate(request);
        return request;
    }

    /// <summary>
    /// Generates the Python wrapper entry: the package root becomes both the
    /// working directory and the first sys.path entry, and the target script
    /// runs as __main__ via runpy. A JSON object or array of arguments is
    /// surfaced to the script as sys.argv (string values are passed through,
    /// others are JSON-encoded). JSON escaping produces a Python-safe string
    /// literal, and script names are restricted by
    /// <see cref="ExecutionLimits.IsSafeFileName"/>, so neither can inject code
    /// into the wrapper.
    /// </summary>
    private static string BuildWrapperCode(string scriptRelativeName, JsonElement? arguments)
    {
        string scriptLiteral = JsonSerializer.Serialize($"/input/{scriptRelativeName}", LiteralOptions);
        string rawAssignment = arguments.HasValue
            ? $"raw = json.loads({JsonSerializer.Serialize(arguments.Value.GetRawText(), LiteralOptions)})"
            : "raw = None";
        return $"""
            import json, os, runpy, sys
            os.chdir("/input")
            sys.path.insert(0, "/input")
            _script_dir = os.path.dirname({scriptLiteral})
            if _script_dir not in sys.path:
                sys.path.insert(0, _script_dir)
            {rawAssignment}
            extra = []
            if isinstance(raw, dict):
                extra = [value if isinstance(value, str) else json.dumps(value) for value in raw.values()]
            elif isinstance(raw, list):
                extra = [value if isinstance(value, str) else json.dumps(value) for value in raw]
            elif raw is not None:
                extra = [json.dumps(raw)]
            sys.argv = [{scriptLiteral}, *extra]
            runpy.run_path({scriptLiteral}, run_name="__main__")
            """;
    }

    /// <summary>
    /// Generates the shell wrapper entry: the working directory becomes /input
    /// and the target script runs under bash via exec. Arguments follow the same
    /// string-pass-through / JSON-encode rules as the other wrappers and are
    /// embedded as single-quoted shell literals with embedded quotes escaped,
    /// so argument values cannot inject commands into the wrapper.
    /// </summary>
    private static string BuildShellWrapperCode(string scriptRelativeName, JsonElement? arguments)
    {
        var extra = new List<string>();
        if (arguments.HasValue)
        {
            JsonElement raw = arguments.Value;
            IEnumerable<JsonElement> values = raw.ValueKind switch
            {
                JsonValueKind.Object => raw.EnumerateObject().Select(property => property.Value),
                JsonValueKind.Array => raw.EnumerateArray(),
                _ => [raw]
            };
            foreach (JsonElement value in values)
            {
                extra.Add(value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : JsonSerializer.Serialize(value, JsonOptions));
            }
        }
        string invocation = new[] { $"/input/{scriptRelativeName}" }
            .Concat(extra)
            .Select(ShellQuote)
            .Aggregate((left, right) => $"{left} {right}");
        return $"""
            cd /input
            exec /bin/bash {invocation}
            """;
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>
    /// Generates the JavaScript wrapper entry: the target script is imported by
    /// its absolute /input URL (so both CommonJS .js and ESM .mjs work regardless
    /// of the sandbox working directory) and arguments are surfaced as
    /// process.argv with the same string-pass-through / JSON-encode rules as the
    /// Python wrapper. The default JSON encoder escapes non-ASCII, which keeps
    /// the embedded literals valid JavaScript.
    /// </summary>
    private static string BuildJavaScriptWrapperCode(string scriptRelativeName, JsonElement? arguments)
    {
        string scriptLiteral = JsonSerializer.Serialize(scriptRelativeName, JsonOptions);
        string rawAssignment = arguments.HasValue
            ? $"const raw = {JsonSerializer.Serialize(arguments.Value, JsonOptions)};"
            : "const raw = null;";
        return $$"""
            import { pathToFileURL } from "node:url";
            {{rawAssignment}}
            let extra = [];
            if (Array.isArray(raw)) {
              extra = raw.map(value => typeof value === "string" ? value : JSON.stringify(value));
            } else if (raw !== null && typeof raw === "object") {
              extra = Object.values(raw).map(value => typeof value === "string" ? value : JSON.stringify(value));
            } else if (raw !== null) {
              extra = [JSON.stringify(raw)];
            }
            process.argv = [process.argv[0], {{scriptLiteral}}, ...extra];
            await import(pathToFileURL("/input/{{scriptRelativeName}}").href);
            """;
    }
}
