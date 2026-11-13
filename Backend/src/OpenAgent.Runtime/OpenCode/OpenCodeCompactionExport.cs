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
        ProcessStartInfo start = new(command.FileName)
        {
            WorkingDirectory = agent.Home, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        foreach (string argument in command.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (KeyValuePair<string, string> variable in options.Environment!) start.Environment[variable.Key] = variable.Value;
        using Process process = new() { StartInfo = start };
        if (!process.Start()) throw new IOException("Native session export did not start.");
        process.StandardInput.Close();
        Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct);
        try
        {
            using MemoryStream buffer = new();
            byte[] block = new byte[8192];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(block, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 32 * 1024 * 1024) throw new IOException("Native session export exceeds the audit limit.");
                buffer.Write(block, 0, count);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Native session export failed.");
            buffer.Position = 0;
            using JsonDocument document = await JsonDocument.ParseAsync(buffer, cancellationToken: ct).ConfigureAwait(false);
            return ReadSummaries(document.RootElement, sessionId, agent.ReportedCompactions);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
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
