using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Runtime.Acp.Protocol;

namespace OpenAgent.Runtime.OpenCode;

/// <summary>OpenCode v1 does not emit ACP compaction events. Read native summary metadata through its export CLI.</summary>
internal static class OpenCodeCompactionExport
{
    internal static async Task<IReadOnlyList<ContextSummary>> ReadAsync(
        OpenCodeLaunchSpec launch, RuntimeInvocation invocation, OpenCodeAgentProcess agent,
        IReadOnlyList<string> allowedTools, string sessionId, CancellationToken ct)
    {
        if (sessionId.Length > 256 || sessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new InvalidOperationException("Native session ID cannot be exported.");
        OpenCodeLaunchSpec command = OpenCodeProcess.WithArguments(launch, ["export", sessionId]);
        Acp.Processes.AcpProcessOptions options = OpenCodeProcess.Create(command, invocation, allowedTools, agent.Home);
        string output = Path.Combine(options.Environment!["TMPDIR"], "compaction-export-" + Guid.NewGuid().ToString("N") + ".json");
        FileStreamOptions createOptions = new()
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows()) createOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // Pre-create in the private runtime directory; shell redirection preserves mode 0600.
        using (FileStream file = new(output, createOptions)) { }
        try
        {
            ProcessStartInfo start = CreateExportStartInfo(launch, command, agent.Home, options.Environment, output, sessionId);
            using Process process = new() { StartInfo = start };
            if (!process.Start()) throw new IOException("Native session export did not start.");
            process.StandardInput.Close();
            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
            try
            {
                Task exited = process.WaitForExitAsync(ct);
                while (!exited.IsCompleted)
                {
                    if (new FileInfo(output).Length > MaxExportBytes) throw new IOException("Native session export exceeds the audit limit.");
                    await Task.WhenAny(exited, Task.Delay(25, ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                await exited.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("Native session export failed.");
                await using FileStream file = File.OpenRead(output);
                if (file.Length > MaxExportBytes) throw new IOException("Native session export exceeds the audit limit.");
                using JsonDocument document = await JsonDocument.ParseAsync(file, cancellationToken: ct).ConfigureAwait(false);
                return ReadSummaries(document.RootElement, sessionId, agent.ReportedCompactions);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await stderr.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    private const long MaxExportBytes = 32 * 1024 * 1024;

    private static ProcessStartInfo CreateExportStartInfo(
        OpenCodeLaunchSpec launch, OpenCodeLaunchSpec command, string home,
        IReadOnlyDictionary<string, string> environment, string output, string sessionId)
    {
        // OpenCode v1 can exit before flushing stdout pipes. Its stdout must be a regular file,
        // rather than a pipe copied to a file by the reader. Only fixed shell programs are used;
        // paths and arguments are passed as positional arguments or quoted environment values.
        ProcessStartInfo start = new()
        {
            WorkingDirectory = home, UseShellExecute = false,
            RedirectStandardOutput = false, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.Environment.Clear();
        foreach (KeyValuePair<string, string> variable in environment) start.Environment[variable.Key] = variable.Value;
        start.Environment["OPENAGENT_EXPORT_FILE"] = output;
        if (OperatingSystem.IsWindows())
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            start.Environment["OPENAGENT_EXPORT_EXECUTABLE"] = launch.ShellFallback ? launch.Source! : launch.FileName;
            start.Environment["OPENAGENT_EXPORT_SESSION"] = sessionId;
            start.Arguments = "/d /v:off /s /c \"\"%OPENAGENT_EXPORT_EXECUTABLE%\" export \"%OPENAGENT_EXPORT_SESSION%\" > \"%OPENAGENT_EXPORT_FILE%\"\"";
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exec \"$@\" > \"$OPENAGENT_EXPORT_FILE\"");
            start.ArgumentList.Add("openagent-export");
            start.ArgumentList.Add(command.FileName);
            foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        }
        return start;
    }

    internal static IReadOnlyList<ContextSummary> ReadSummaries(JsonElement export, string sessionId, HashSet<string> reported)
    {
        if (export.GetProperty("info").GetProperty("id").GetString() != sessionId)
            throw new InvalidOperationException("Native export session identity mismatch.");
        AcpCompactionMapper mapper = new(sessionId);
        List<ContextSummary> result = [];
        foreach (JsonElement message in export.GetProperty("messages").EnumerateArray())
        {
            JsonElement info = message.GetProperty("info");
            if (info.GetProperty("role").GetString() != "assistant"
                || !info.TryGetProperty("summary", out JsonElement summary) || summary.ValueKind != JsonValueKind.True) continue;
            string id = info.GetProperty("id").GetString()!;
            if (reported.Contains(id)) continue;
            string text = string.Concat(message.GetProperty("parts").EnumerateArray()
                .Where(part => part.GetProperty("type").GetString() == "text")
                .Select(part => part.GetProperty("text").GetString()));
            bool failed = info.TryGetProperty("error", out JsonElement error) && error.ValueKind is not JsonValueKind.Null;
            string status = failed ? "failed"
                : info.GetProperty("time").TryGetProperty("completed", out _) ? "completed" : "in_progress";
            ContextSummary audit = mapper.Map(new CompactionUpdate(id, status)
            {
                Summary = new ContentBlock[] { new TextContent(text) },
                Error = failed ? new Optional<string>("Native compaction failed") : Optional<string>.Null
            });
            result.Add(audit);
            if (status != "in_progress") reported.Add(id);
        }
        return result;
    }
}
