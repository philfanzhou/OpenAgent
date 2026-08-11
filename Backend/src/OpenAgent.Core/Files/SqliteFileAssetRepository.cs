using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;

namespace OpenAgent.Core.Files;

internal sealed class SqliteFileAssetRepository : IFileAssetRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteFileAssetRepository(IOptions<FileAssetOptions> options)
    {
        _connectionString = options.Value.MetadataConnectionString ?? string.Empty;
    }

    public async Task CreateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO FileAssets
                (FileId, TenantId, OwnerUserId, FileName, MediaType, Length, Sha256, ObjectKey, Source, State, CreatedAt)
            VALUES
                ($FileId, $TenantId, $OwnerUserId, $FileName, $MediaType, $Length, $Sha256, $ObjectKey, $Source, $State, $CreatedAt);
            """;
        AddAssetParameters(command, asset);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(FileAsset asset, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE FileAssets
            SET ObjectKey = $ObjectKey, State = $State
            WHERE FileId = $FileId;
            """;
        command.Parameters.AddWithValue("$FileId", asset.FileId);
        command.Parameters.AddWithValue("$ObjectKey", asset.ObjectKey);
        command.Parameters.AddWithValue("$State", (int)asset.State);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FileAsset?> GetAsync(string fileId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT FileId, TenantId, OwnerUserId, FileName, MediaType, Length, Sha256, ObjectKey, Source, State, CreatedAt
            FROM FileAssets
            WHERE FileId = $FileId;
            """;
        command.Parameters.AddWithValue("$FileId", fileId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAsset(reader)
            : null;
    }

    public async Task AddConversationReferenceAsync(
        string conversationId,
        string fileId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO ConversationFileReferences (ConversationId, FileId, CreatedAt)
            VALUES ($ConversationId, $FileId, $CreatedAt);
            """;
        command.Parameters.AddWithValue("$ConversationId", conversationId);
        command.Parameters.AddWithValue("$FileId", fileId);
        command.Parameters.AddWithValue("$CreatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS FileAssets (
                    FileId TEXT PRIMARY KEY,
                    TenantId TEXT NOT NULL,
                    OwnerUserId TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    MediaType TEXT NOT NULL,
                    Length INTEGER NOT NULL,
                    Sha256 TEXT NOT NULL,
                    ObjectKey TEXT NOT NULL,
                    Source INTEGER NOT NULL,
                    State INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ConversationFileReferences (
                    ConversationId TEXT NOT NULL,
                    FileId TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    PRIMARY KEY (ConversationId, FileId)
                );
                CREATE INDEX IF NOT EXISTS IX_FileAssets_TenantId ON FileAssets (TenantId);
                CREATE INDEX IF NOT EXISTS IX_ConversationFileReferences_FileId ON ConversationFileReferences (FileId);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static void AddAssetParameters(SqliteCommand command, FileAsset asset)
    {
        command.Parameters.AddWithValue("$FileId", asset.FileId);
        command.Parameters.AddWithValue("$TenantId", asset.TenantId);
        command.Parameters.AddWithValue("$OwnerUserId", asset.OwnerUserId);
        command.Parameters.AddWithValue("$FileName", asset.FileName);
        command.Parameters.AddWithValue("$MediaType", asset.MediaType);
        command.Parameters.AddWithValue("$Length", asset.Length);
        command.Parameters.AddWithValue("$Sha256", asset.Sha256);
        command.Parameters.AddWithValue("$ObjectKey", asset.ObjectKey);
        command.Parameters.AddWithValue("$Source", (int)asset.Source);
        command.Parameters.AddWithValue("$State", (int)asset.State);
        command.Parameters.AddWithValue("$CreatedAt", asset.CreatedAt.ToString("O"));
    }

    private static FileAsset ReadAsset(SqliteDataReader reader) => new()
    {
        FileId = reader.GetString(0),
        TenantId = reader.GetString(1),
        OwnerUserId = reader.GetString(2),
        FileName = reader.GetString(3),
        MediaType = reader.GetString(4),
        Length = reader.GetInt64(5),
        Sha256 = reader.GetString(6),
        ObjectKey = reader.GetString(7),
        Source = (FileAssetSource)reader.GetInt32(8),
        State = (FileAssetState)reader.GetInt32(9),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture)
    };
}
