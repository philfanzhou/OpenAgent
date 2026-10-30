using System.Security.Cryptography;
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
    FileTransferGate gate) : IAsyncDisposable
{
    private readonly SemaphoreSlim _operations = new(1);
    private readonly Dictionary<string, RuntimeWorkspaceFile> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileAsset> _exports = new(StringComparer.Ordinal);
    private IRuntimeWorkspaceFiles? _workspace;
    private CancellationTokenSource? _lifetime;
    private long _inputBytes;
    private long _outputBytes;
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
            return new RuntimeFilePreparation(_imports.Values.ToArray(), this);
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
                FileAssetContent content = await files.ReadAsync(fileId, scope, linked.Token,
                    Math.Min(assets.Value.MaxFileSizeBytes, options.Value.MaxInputBytes)).ConfigureAwait(false);
                EnsureActive(linked.Token);
                if (content.Data.LongLength != asset.Length
                    || !Convert.ToHexStringLower(SHA256.HashData(content.Data)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The input file failed its integrity check.");
                }
                RuntimeWorkspaceFile imported = await workspace.ImportAsync(
                    new RuntimeFile(asset.FileId, asset.FileName, asset.MediaType, asset.Length, asset.Sha256),
                    content.Data, linked.Token).ConfigureAwait(false);
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
                string name = string.IsNullOrWhiteSpace(fileName) ? snapshot.FileName : fileName;
                if (name != Path.GetFileName(name) || name.Contains('\\') || name.Any(char.IsControl)
                    || name.Contains(':') || name is "." or "..")
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The export file name is not allowed.");
                }
                string key = snapshot.RelativePath + "\0" + snapshot.Sha256 + "\0" + name;
                if (_exports.TryGetValue(key, out FileAsset? cached))
                {
                    return cached;
                }
                if (_exports.Count >= options.Value.MaxOutputFiles || snapshot.Length > options.Value.MaxOutputBytes - _outputBytes)
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The turn's output file limit was exceeded.");
                }
                FileAsset asset = await files.UploadAsync(new FileAssetCreateRequest
                {
                    FileName = name, Source = FileAssetSource.Agent
                }, snapshot.Content, execution.Scope!, linked.Token).ConfigureAwait(false);
                if (asset.Length != snapshot.Length || !asset.Sha256.Equals(snapshot.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AgentException(AgentErrorCode.InvalidRequest, "The exported file failed its integrity check.");
                }
                EnsureActive(linked.Token);
                await files.EnsureReferencesAsync([asset.FileId], execution.Scope!, linked.Token).ConfigureAwait(false);
                _exports[key] = asset;
                _outputBytes += asset.Length;
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
