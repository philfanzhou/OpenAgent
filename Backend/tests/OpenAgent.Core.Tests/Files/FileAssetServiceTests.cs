using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public class FileAssetServiceTests
{
    [Fact]
    public async Task UploadAsync_StoresAssetBeforeItIsReferencedByAConversationMessage()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("# Hello"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = "notes.md",
                MediaType = "text/markdown",
                Source = FileAssetSource.UserUpload
            },
            content,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal("tenant-a", asset.TenantId);
        Assert.Equal("user-a", asset.OwnerUserId);
        Assert.Equal(TenantObjectKey(asset.FileId), asset.ObjectKey);
        Assert.Equal("tenant-a", objects.LastRequest?.TenantId);
        Assert.Equal("user-a", objects.LastRequest?.UserId);
        Assert.Equal("# Hello", System.Text.Encoding.UTF8.GetString(objects.LastContent));
        Assert.Empty(repository.References);
    }

    [Fact]
    public async Task ReadAsync_NoConversationReference_RejectsWithoutReadingObject()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task GetReferencedAsync_ReturnsMetadataWithoutReadingObject()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        FileAsset? result = await service.GetReferencedAsync(
            asset.FileId,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(asset.FileId, result.FileId);
        Assert.Equal(0, objects.ReadCount);
    }

    [Theory]
    [InlineData("tenant-b", "user-a")]
    [InlineData("tenant-a", "user-b")]
    public async Task ReadAsync_NotOwner_RejectsEvenWithReference(string tenantId, string userId)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = tenantId,
                UserId = userId,
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task ReadTextAsync_NonTextFile_RejectsModelFunctionRead()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = [1, 2, 3] };
        FileAsset asset = CreateAsset("image.png", "image/png");
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadTextAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task EnsureReferencesAsync_OnlyAssociatesOwnedFiles()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset mine = CreateAsset("notes.md", "text/markdown");
        FileAsset other = new()
        {
            FileId = "file-other",
            TenantId = "tenant-b",
            OwnerUserId = "user-a",
            FileName = "other.md",
            MediaType = "text/markdown",
            Length = 3,
            Sha256 = "sha",
            ObjectKey = "files/other",
            Source = FileAssetSource.UserUpload,
            State = FileAssetState.Ready,
            CreatedAt = DateTimeOffset.UtcNow
        };
        repository.Assets[mine.FileId] = mine;
        repository.Assets[other.FileId] = other;
        IFileAssetService service = CreateService(repository, objects);

        await service.EnsureReferencesAsync(
            [mine.FileId, other.FileId],
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None);

        Assert.Contains($"conversation-a:{mine.FileId}", repository.References);
        Assert.DoesNotContain($"conversation-a:{other.FileId}", repository.References);
    }

    [Fact]
    public async Task GetAsync_DifferentTenant_ReturnsNotFound()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);

        FileAsset? result = await service.GetAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = "tenant-b",
                UserId = "user-a"
            },
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureReferencesAsync_IsIdempotent()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);
        FileAssetScope scope = new()
        {
            TenantId = "tenant-a",
            UserId = "user-a",
            ConversationId = "conversation-a"
        };

        await service.EnsureReferencesAsync([asset.FileId], scope, CancellationToken.None);
        await service.EnsureReferencesAsync([asset.FileId], scope, CancellationToken.None);

        Assert.Single(repository.References);
        Assert.Contains($"conversation-a:{asset.FileId}", repository.References);

    }

    [Fact]
    public async Task UploadAsync_DrawioFile_StoresEditableDiagram()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128,
                AllowedMediaTypes = ["application/vnd.jgraph.mxfile"],
                AllowedExtensions = [".drawio"]
            }));
        await using var content = new MemoryStream("<mxfile><diagram /></mxfile>"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = "circuit.drawio",
                MediaType = "application/vnd.jgraph.mxfile",
                Source = FileAssetSource.Agent
            },
            content,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None);

        Assert.Equal("circuit.drawio", asset.FileName);
        Assert.Equal("application/vnd.jgraph.mxfile", asset.MediaType);
        Assert.Equal("<mxfile><diagram /></mxfile>", System.Text.Encoding.UTF8.GetString(objects.LastContent));
    }

    [Fact]
    public async Task ReadObjectTextAsync_TenantScopedKey_ReturnsDecodedText()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        objects.ContentsByKey[TenantObjectKey("reports/notes.md")] = "# Hello"u8.ToArray();
        IFileAssetService service = CreateService(repository, objects);

        string content = await service.ReadObjectTextAsync(
            TenantObjectKey("reports/notes.md"),
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal("# Hello", content);
    }

    [Fact]
    public async Task ReadObjectTextAsync_ForeignTenantPartition_RejectsBeforeReading()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = "# secret"u8.ToArray() };
        IFileAssetService service = CreateService(repository, objects);
        string foreignKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-b")}" +
            "/users/user-a/notes.md";

        await Assert.ThrowsAsync<TenantDataIsolationException>(() => service.ReadObjectTextAsync(
            foreignKey,
            Scope("conversation-a"),
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("reports/../secret.txt")]
    public async Task ReadObjectTextAsync_InvalidKeySegments_Rejects(string objectKey)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = "# secret"u8.ToArray() };
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.ReadObjectTextAsync(
            objectKey,
            Scope("conversation-a"),
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task ReadObjectTextAsync_NonUtf8Bytes_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore
        {
            ContentsByKey =
            {
                [TenantObjectKey("notes.md")] = [0xFF, 0xFE, 0xFF, 0xFE]
            }
        };
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.ReadObjectTextAsync(
            TenantObjectKey("notes.md"),
            Scope("conversation-a"),
            CancellationToken.None));
    }

    [Fact]
    public async Task ReadObjectTextAsync_OversizeContent_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore
        {
            ContentsByKey = { [TenantObjectKey("big.log")] = new byte[256] }
        };
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.ReadObjectTextAsync(
            TenantObjectKey("big.log"),
            Scope("conversation-a"),
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task CompressAsync_ObjectKeyItems_WritesZipArchiveWithEntryNames()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        objects.ContentsByKey[TenantObjectKey("docs/a.txt")] = "alpha"u8.ToArray();
        objects.ContentsByKey[TenantObjectKey("img/b.csv")] = "1;2;3"u8.ToArray();
        IFileAssetService service = CreateService(repository, objects);

        FileArchiveResult result = await service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.zip",
                Items =
                [
                    new FileArchiveItem
                    {
                        ObjectKey = TenantObjectKey("docs/a.txt"),
                        FileName = "text/readme.txt"
                    },
                    new FileArchiveItem { ObjectKey = TenantObjectKey("img/b.csv") }
                ]
            },
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(2, result.FileCount);
        Assert.True(result.Length > 0);
        Assert.Equal(FileAssetSource.Agent, result.Asset.Source);
        Assert.Equal(FileAssetState.Ready, result.Asset.State);
        Assert.Equal(result.Asset.ObjectKey, result.ObjectKey);
        Assert.Same(result.Asset, repository.Assets[result.Asset.FileId]);
        Assert.Equal("tenant-a", objects.LastRequest?.TenantId);
        Assert.Equal("user-a", objects.LastRequest?.UserId);
        Assert.Equal("bundle.zip", objects.LastRequest?.FileName);
        Assert.Equal("application/zip", objects.LastRequest?.MediaType);
        Assert.StartsWith("archive-", objects.LastRequest?.FileId);

        using var archiveStream = new MemoryStream(objects.LastContent);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        string[] entryNames = archive.Entries.Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] expectedNames = ["b.csv", "text/readme.txt"];
        Assert.Equal(expectedNames, entryNames);

        ZipArchiveEntry readme = archive.GetEntry("text/readme.txt")!;
        using var reader = new StreamReader(readme.Open());
        Assert.Equal("alpha", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task CompressAsync_FileIdItem_IncludesReferencedAssetContent()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        objects.ContentsByKey[asset.ObjectKey] = "# Report"u8.ToArray();
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        FileArchiveResult result = await service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "report.zip",
                Items = [new FileArchiveItem { FileId = asset.FileId }]
            },
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(1, result.FileCount);
        using var archiveStream = new MemoryStream(objects.LastContent);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        ZipArchiveEntry entry = Assert.Single(archive.Entries);
        Assert.Equal("notes.md", entry.FullName);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("# Report", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task CompressAsync_UnreferencedFileIdItem_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "report.zip",
                Items = [new FileArchiveItem { FileId = asset.FileId }]
            },
            Scope("conversation-a"),
            CancellationToken.None));

        Assert.Null(objects.LastRequest);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task CompressAsync_ItemWithoutExactlyOneIdentifier_Rejects(
        bool withFileId,
        bool withObjectKey)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.zip",
                Items =
                [
                    new FileArchiveItem
                    {
                        FileId = withFileId ? "file-a" : null,
                        ObjectKey = withObjectKey ? TenantObjectKey("a.txt") : null
                    }
                ]
            },
            Scope("conversation-a"),
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task CompressAsync_DuplicateEntryName_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        objects.ContentsByKey[TenantObjectKey("a.txt")] = "one"u8.ToArray();
        objects.ContentsByKey[TenantObjectKey("b/c.txt")] = "two"u8.ToArray();
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.zip",
                Items =
                [
                    new FileArchiveItem { ObjectKey = TenantObjectKey("a.txt") },
                    new FileArchiveItem { ObjectKey = TenantObjectKey("b/c.txt"), FileName = "a.txt" }
                ]
            },
            Scope("conversation-a"),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("")]
    public async Task CompressAsync_UnsafeEntryName_Rejects(string fileName)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        objects.ContentsByKey[TenantObjectKey("a.txt")] = "data"u8.ToArray();
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.zip",
                Items = [new FileArchiveItem { ObjectKey = TenantObjectKey("a.txt"), FileName = fileName }]
            },
            Scope("conversation-a"),
            CancellationToken.None));
    }

    [Fact]
    public async Task CompressAsync_OutputNameWithoutZipExtension_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.rar",
                Items = [new FileArchiveItem { ObjectKey = TenantObjectKey("a.txt") }]
            },
            Scope("conversation-a"),
            CancellationToken.None));
    }

    [Fact]
    public async Task CompressAsync_TooManyItems_RejectsWithoutReadingObjects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128,
                MaxArchiveFileCount = 1
            }));

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.zip",
                Items =
                [
                    new FileArchiveItem { ObjectKey = TenantObjectKey("a.txt") },
                    new FileArchiveItem { ObjectKey = TenantObjectKey("b.txt") }
                ]
            },
            Scope("conversation-a"),
            CancellationToken.None));

        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task CompressAsync_TotalInputOverLimit_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        objects.ContentsByKey[TenantObjectKey("a.txt")] = "aaa"u8.ToArray();
        objects.ContentsByKey[TenantObjectKey("b.txt")] = "bbb"u8.ToArray();
        IFileAssetService service = new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128,
                MaxArchiveInputBytes = 4
            }));

        await Assert.ThrowsAsync<AgentException>(() => service.CompressAsync(
            new FileArchiveRequest
            {
                OutputName = "bundle.zip",
                Items =
                [
                    new FileArchiveItem { ObjectKey = TenantObjectKey("a.txt") },
                    new FileArchiveItem { ObjectKey = TenantObjectKey("b.txt") }
                ]
            },
            Scope("conversation-a"),
            CancellationToken.None));
    }

    private static FileAssetScope Scope(string conversationId) => new()
    {
        TenantId = "tenant-a",
        UserId = "user-a",
        ConversationId = conversationId
    };

    private static string TenantObjectKey(string tail) =>
        $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}/users/user-a/{tail}";

    [Fact]
    public async Task CreateTransferUrlAsync_ReturnsActualObjectKeyForOwnedReadyAsset()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset("notes.md", "text/markdown");
        repository.Assets[asset.FileId] = asset;
        IFileAssetService service = CreateService(repository, objects);

        FileObjectAccessReference result = await service.CreateTransferUrlAsync(
            asset.FileId,
            new FileAssetScope { TenantId = "tenant-a", UserId = "user-a" },
            CancellationToken.None);

        Assert.Equal(asset.ObjectKey, result.ObjectKey);
        Assert.Equal($"https://storage.example/{asset.ObjectKey}", result.Url);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(100));
        Assert.Equal(asset.ObjectKey, objects.LastAccessObjectKey);
    }

    private static IFileAssetService CreateService(
        RecordingFileAssetRepository repository,
        RecordingFileObjectStore objects) => new FileAssetService(
            repository,
            objects,
            Options.Create(new FileAssetOptions
            {
                Enabled = true,
                MaxFileSizeBytes = 1024,
                MaxFunctionReadBytes = 128
            }));

    private static FileAsset CreateAsset(string fileName, string mediaType) => new()
    {
        FileId = "file-a",
        TenantId = "tenant-a",
        OwnerUserId = "user-a",
        FileName = fileName,
        MediaType = mediaType,
        Length = 3,
        Sha256 = "sha",
        ObjectKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}" +
            "/users/user-a/file-a",
        Source = FileAssetSource.UserUpload,
        State = FileAssetState.Ready,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
