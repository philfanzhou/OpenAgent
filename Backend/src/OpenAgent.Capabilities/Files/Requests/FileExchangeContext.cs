using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Capabilities.Files.Services;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Files.Requests;

/// <summary>Owns only one turn's file operations; never retained in the runtime process pool.</summary>
internal sealed class FileExchangeContext(
    IFileAssetService files,
    FileAssetExecutionContext execution,
    IOptions<FileAssetOptions> assets,
    IOptions<FileExchangeOptions> options,
    FileTransferGate gate,
    ILogger<FileExchangeContext> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1);
    private readonly Dictionary<string, RuntimeWorkspaceFile> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileAsset> _pendingExports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileAsset> _savedOutputs = new(StringComparer.Ordinal);
    private IRuntimeWorkspaceFiles? _workspace;
    private CancellationTokenSource? _lifetime;
    private long _inputBytes;
    private long _outputBytes;
    private int _outputFiles;
    private int _closed;

    internal bool Enabled => assets.Value.Enabled && options.Value.Enabled && execution.Scope?.ConversationId is not null;

    internal async Task<RuntimeFilePreparation> PrepareAsync(
        IRuntimeWorkspaceFiles workspace, IReadOnlyList<FileAsset> inputs, CancellationToken ct)
    {
        if (!Enabled || _workspace is not null || Volatile.Read(ref _closed) != 0)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "File exchange is not available for this turn.");
        }
        _workspace = workspace;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            foreach (FileAsset input in inputs.DistinctBy(file => file.FileId))
            {
                await ImportAsync(input.FileId, ct).ConfigureAwait(false);
            }
            IReadOnlyList<RuntimeWorkspaceFile> outputs = await RestoreOutputsAsync(ct).ConfigureAwait(false);
            return new RuntimeFilePreparation(_imports.Values.ToArray(), this)
            {
                Outputs = outputs,
                SaveOutputsAsync = SaveOutputsAsync
            };
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<RuntimeWorkspaceFile> ImportAsync(string fileId, CancellationToken ct)
    {
        IRuntimeWorkspaceFiles workspace = _workspace
            ?? throw new AgentException(AgentErrorCode.InvalidRequest, "The runtime workspace is not attached.");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime!.Token);
        await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureActive(linked.Token);
            FileAssetScope scope = execution.Scope!;
            FileAsset asset = await files.GetReferencedAsync(fileId, scope, linked.Token).ConfigureAwait(false)
                ?? throw new AgentException(AgentErrorCode.NotFound, "The file is not available in this conversation.");
            if (asset.State != FileAssetState.Ready)
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, "The file is not ready.");
            }
            bool known = _imports.ContainsKey(fileId);
            if ((!known && _imports.Count >= options.Value.MaxInputFiles)
                || asset.Length > assets.Value.MaxFileSizeBytes
                || (!known && asset.Length > options.Value.MaxInputBytes - _inputBytes))
            {
                throw new AgentException(AgentErrorCode.InvalidRequest, "The turn's input file limit was exceeded.");
            }
            await gate.Transfers.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                RuntimeWorkspaceFile imported = await RestoreAsync(workspace, asset, outputPath: null, linked.Token).ConfigureAwait(false);
                if (!known)
                {
                    _inputBytes += asset.Length;
                }
                _imports[fileId] = imported;
                return imported;
            }
            finally
            {
                gate.Transfers.Release();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<IReadOnlyList<RuntimeWorkspaceFile>> RestoreOutputsAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        // Inspect a bounded set even when a long conversation has many historical versions.
        int candidates = options.Value.MaxOutputFiles * 4;
        IReadOnlyList<FileAsset> referenced = await files.ListRecentOutputsAsync(execution.Scope!, candidates, ct).ConfigureAwait(false);
        foreach (FileAsset asset in referenced)
        {
            if (asset.State == FileAssetState.Ready && asset.RuntimeOutputPath is { } path)
            {
                _savedOutputs.TryAdd(path, asset);
            }
        }

        List<RuntimeWorkspaceFile> restored = [];
        long remaining = options.Value.MaxOutputBytes;
        foreach ((string path, FileAsset asset) in _savedOutputs.ToArray())
        {
            if (restored.Count == options.Value.MaxOutputFiles)
            {
                break;
            }
            if (asset.Length > remaining || asset.Length > assets.Value.MaxFileSizeBytes)
            {
                continue;
            }
            await gate.Transfers.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                restored.Add(await RestoreAsync(_workspace!, asset, path, ct).ConfigureAwait(false));
                remaining -= asset.Length;
            }
            catch (Exception error) when (IsUnavailableOutput(error)
                || error is AgentException { ErrorCode: AgentErrorCode.DependencyUnavailable })
            {
                ct.ThrowIfCancellationRequested();
                // A regenerated file must be uploaded even if its contents match this asset.
                _savedOutputs.Remove(path);
                LogSkippedOutput("restore", error, asset.FileId);
            }
            finally
            {
                gate.Transfers.Release();
            }
        }
        FileAssetMetrics.RestoreDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return restored;
    }

    private Task<RuntimeWorkspaceFile> RestoreAsync(IRuntimeWorkspaceFiles workspace, FileAsset asset, string? outputPath, CancellationToken ct) =>
        workspace.RestoreAsync(new RuntimeFile(asset.FileId, asset.FileName, asset.MediaType, asset.Length, asset.Sha256), outputPath,
            (destination, token) => files.CopyReferencedToAsync(asset.FileId, execution.Scope!, destination, asset.Length, token), ct);

    private async Task SaveOutputsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.Value.AutoSaveTimeoutMs);
        try
        {
            foreach (string path in _workspace!.EnumerateOutputPaths())
            {
                budget.Token.ThrowIfCancellationRequested();
                if (_outputFiles >= options.Value.MaxOutputFiles || _outputBytes >= options.Value.MaxOutputBytes)
                {
                    logger.LogWarning("Automatic output saving stopped at the turn limit. TraceId={TraceId}", execution.Turn?.TraceId);
                    break;
                }

                try
                {
                    await ExportAsync(path, fileName: null, budget.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (IsUnavailableOutput(error))
                {
                    ct.ThrowIfCancellationRequested();
                    LogSkippedOutput("save", error);
                }
            }
        }
        catch (OperationCanceledException error) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            LogSkippedOutput("save timeout", error);
        }
        catch (AgentException error) when (error.ErrorCode == AgentErrorCode.DependencyUnavailable)
        {
            ct.ThrowIfCancellationRequested();
            LogSkippedOutput("save", error);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The enumeration itself can fail before an individual path is returned.
            ct.ThrowIfCancellationRequested();
            LogSkippedOutput("enumerate", error);
        }
    }

    private static bool IsUnavailableOutput(Exception error) => error is IOException or UnauthorizedAccessException
        or AgentException { ErrorCode: AgentErrorCode.InvalidRequest or AgentErrorCode.NotFound };

    private void LogSkippedOutput(string operation, Exception error, string? fileId = null) =>
        logger.LogWarning("Automatic output {Operation} skipped. TraceId={TraceId} FileId={FileId} Reason={Reason}",
            operation, execution.Turn?.TraceId, fileId,
            error is AgentException agent ? agent.ErrorCode.ToString() : error.GetType().Name);

    internal async Task<FileAsset> ExportAsync(string path, string? fileName, CancellationToken ct)
    {
        IRuntimeWorkspaceFiles workspace = _workspace
            ?? throw new AgentException(AgentErrorCode.InvalidRequest, "The runtime workspace is not attached.");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime!.Token);
        await _operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            EnsureActive(linked.Token);
            await gate.Transfers.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await using RuntimeOutputFile snapshot = await workspace.SnapshotAsync(path, assets.Value.MaxFileSizeBytes, linked.Token).ConfigureAwait(false);
                EnsureActive(linked.Token);
                bool hasSuggestedName = !string.IsNullOrWhiteSpace(fileName);
                string name = hasSuggestedName ? fileName! : snapshot.FileName;
                FileAssetService.ValidateFileNameLength(name);
                if (name != Path.GetFileName(name) || name.Contains('\\') || name.Any(char.IsControl)
                    || name.Contains(':') || name is "." or "..")
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The export file name is not allowed.");
                }
                if (_savedOutputs.TryGetValue(snapshot.RelativePath, out FileAsset? saved)
                    && (!hasSuggestedName || saved.FileName == name) && saved.Length == snapshot.Length
                    && saved.Sha256.Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return saved;
                }

                string key = snapshot.RelativePath + "\0" + snapshot.Sha256 + "\0" + name;
                if (!_pendingExports.TryGetValue(key, out FileAsset? asset))
                {
                    if (_outputFiles >= options.Value.MaxOutputFiles || snapshot.Length > options.Value.MaxOutputBytes - _outputBytes)
                    {
                        throw new AgentException(AgentErrorCode.InvalidRequest, "The turn's output file limit was exceeded.");
                    }
                    asset = await files.UploadAsync(new FileAssetCreateRequest
                    {
                        FileName = name, Source = FileAssetSource.Agent, RuntimeOutputPath = snapshot.RelativePath
                    }, snapshot.Content, execution.Scope!, linked.Token).ConfigureAwait(false);
                    if (asset.Length != snapshot.Length || !asset.Sha256.Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new AgentException(AgentErrorCode.InvalidRequest, "The exported file failed its integrity check.");
                    }
                    // If reference registration fails, retry it for the stored asset without uploading another copy.
                    _pendingExports.Add(key, asset);
                    _outputFiles++;
                    _outputBytes += asset.Length;
                }
                EnsureActive(linked.Token);
                await files.EnsureReferencesAsync([asset.FileId], execution.Scope!, linked.Token).ConfigureAwait(false);
                _pendingExports.Remove(key);
                _savedOutputs[snapshot.RelativePath] = asset;
                return asset;
            }
            finally
            {
                gate.Transfers.Release();
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    private void EnsureActive(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _closed) != 0)
        {
            throw new AgentException(AgentErrorCode.InvalidRequest, "The file exchange lease has expired.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        if (_lifetime is not null)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        if (!await _operations.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
        {
            throw new AgentException(AgentErrorCode.DependencyUnavailable, "File operations did not stop within the cleanup deadline.");
        }
        _workspace = null;
        _operations.Release();
    }
}
