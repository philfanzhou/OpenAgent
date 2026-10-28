using OpenAgent.Capabilities.Tests.TestDoubles;
using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Capabilities.Files.Services;
using Xunit;

namespace OpenAgent.Capabilities.Tests.Files;

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

        // 作用域外/未引用的文件按 NotFound（404）语义拒绝，避免存在性泄露。
        var exception = await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.NotFound, exception.ErrorCode);
        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ThrowsNotFound()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore { Content = [1, 2, 3] };
        IFileAssetService service = CreateService(repository, objects);

        var exception = await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            "missing-file-id",
            new FileAssetScope
            {
                TenantId = "tenant-a",
                UserId = "user-a",
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.NotFound, exception.ErrorCode);
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

        var exception = await Assert.ThrowsAsync<OpenAgent.Contracts.Security.AgentException>(() => service.ReadAsync(
            asset.FileId,
            new FileAssetScope
            {
                TenantId = tenantId,
                UserId = userId,
                ConversationId = "conversation-a"
            },
            CancellationToken.None));

        Assert.Equal(OpenAgent.Contracts.Requests.AgentErrorCode.NotFound, exception.ErrorCode);
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
                MaxFunctionReadBytes = 128
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

    [Theory]
    [InlineData("doc.xml", "application/xml")]
    [InlineData("doc.xml", "text/xml")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("circuit.drawio", "application/vnd.jgraph.mxfile")]
    [InlineData("photo.jps", "image/jpeg")]
    [InlineData("data.json", "text/json")]
    public async Task UploadAsync_CatalogFormats_AcceptedWithCanonicalTypes(string fileName, string mediaType)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("payload"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = fileName,
                MediaType = mediaType,
                Source = FileAssetSource.UserUpload
            },
            content,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal(fileName, asset.FileName);
        Assert.Equal(mediaType, asset.MediaType);
    }

    [Theory]
    [InlineData("data.json", null, "application/json")]
    [InlineData("data.json", "application/octet-stream", "application/json")]
    [InlineData("doc.xml", "application/octet-stream", "application/xml")]
    [InlineData("logo.svg", "application/octet-stream", "image/svg+xml")]
    [InlineData("circuit.drawio", "application/octet-stream", "application/vnd.jgraph.mxfile")]
    [InlineData("photo.jps", "application/octet-stream", "image/jpeg")]
    [InlineData("bundle.zip", "application/x-zip-compressed", "application/zip")]
    public async Task UploadAsync_GenericClientMediaType_InfersCanonicalTypeFromExtension(
        string fileName,
        string? requestedMediaType,
        string expectedMediaType)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("payload"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = fileName,
                MediaType = requestedMediaType,
                Source = FileAssetSource.UserUpload
            },
            content,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal(expectedMediaType, asset.MediaType);
    }

    [Theory]
    [InlineData("macro.xlsm", "application/octet-stream", "application/octet-stream")]
    [InlineData("script.py", null, "application/octet-stream")]
    [InlineData("script.py", "text/x-python", "text/x-python")]
    [InlineData("app.js", "application/octet-stream", "application/octet-stream")]
    [InlineData("run.sh", null, "application/octet-stream")]
    public async Task UploadAsync_ExtensionsOutsideCatalog_AcceptedAsDeclared(
        string fileName,
        string? requestedMediaType,
        string expectedMediaType)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("payload"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest
            {
                FileName = fileName,
                MediaType = requestedMediaType,
                Source = FileAssetSource.UserUpload
            },
            content,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal(fileName, asset.FileName);
        Assert.Equal(expectedMediaType, asset.MediaType);
    }

    [Theory]
    [InlineData("doc.xml", "application/xml")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("circuit.drawio", "application/vnd.jgraph.mxfile")]
    public async Task ReadTextAsync_XmlLikeTextFormats_ReturnsContent(string fileName, string mediaType)
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset(fileName, mediaType);
        objects.ContentsByKey[asset.ObjectKey] = "<payload>ok</payload>"u8.ToArray();
        repository.Assets[asset.FileId] = asset;
        repository.References.Add($"conversation-a:{asset.FileId}");
        IFileAssetService service = CreateService(repository, objects);

        string text = await service.ReadTextAsync(asset.FileId, Scope("conversation-a"), CancellationToken.None);

        Assert.Equal("<payload>ok</payload>", text);
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
    public async Task UploadAsync_HtmlWithoutMediaType_InfersCanonicalType()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("<h1>hi</h1>"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest { FileName = "report.html", Source = FileAssetSource.Agent },
            content,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal("text/html", asset.MediaType);
    }

    [Fact]
    public async Task UploadAsync_CssWithCanonicalMediaType_Succeeds()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("body{}"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest { FileName = "site.css", MediaType = "text/css", Source = FileAssetSource.Agent },
            content,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal("text/css", asset.MediaType);
    }

    [Fact]
    public async Task UploadAsync_DeclaredConcreteMediaType_WinsOverCatalogInference()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        IFileAssetService service = CreateService(repository, objects);
        await using var content = new MemoryStream("<h1>hi</h1>"u8.ToArray());

        FileAsset asset = await service.UploadAsync(
            new FileAssetCreateRequest { FileName = "report.html", MediaType = "text/plain", Source = FileAssetSource.Agent },
            content,
            Scope("conversation-a"),
            CancellationToken.None);

        Assert.Equal(FileAssetState.Ready, asset.State);
        Assert.Equal("text/plain", asset.MediaType);
    }

    private static FileAssetScope Scope(string conversationId) => new()
    {
        TenantId = "tenant-a",
        UserId = "user-a",
        ConversationId = conversationId
    };

    private static string TenantObjectKey(string tail) =>
        $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}/users/user-a/{tail}";

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
