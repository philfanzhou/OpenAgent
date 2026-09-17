using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Files;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Files;
using OpenAgent.Core.Tests.TestDoubles;
using Xunit;

namespace OpenAgent.Core.Tests.Files;

public class FileShareServiceTests
{
    [Fact]
    public async Task CreateAsync_TemporaryMode_DefaultsToShortLifetimeAndUnlimitedDownloads()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository, objects);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.Temporary },
            CancellationToken.None);

        Assert.Null(link.MaxDownloads);
        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 890, 900);
        Assert.StartsWith($"{IFileShareService.RoutePrefix}/", link.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(asset.ObjectKey, link.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_McpAudience_DefaultsTo2HoursAnd2Downloads()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Audience = FileShareAudience.Mcp },
            CancellationToken.None);

        Assert.Equal(2, link.MaxDownloads);
        Assert.Equal(FileShareMode.Custom, link.Mode);
        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalHours, 1.99, 2.01);
        FileShareLinkRecord record = Assert.Single(harness.Shares.Records.Values);
        Assert.Equal(2, record.MaxDownloads);
        Assert.Equal(FileShareMode.Custom, record.Mode);
    }

    [Theory]
    [InlineData(FileShareAudience.User)]
    [InlineData(null)]
    public async Task CreateAsync_UserAudienceOrDefault_DefaultsTo3DaysUnlimited(FileShareAudience? audience)
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Audience = audience },
            CancellationToken.None);

        Assert.Null(link.MaxDownloads);
        Assert.Equal(FileShareMode.Custom, link.Mode);
        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalDays, 2.99, 3.01);
    }

    [Fact]
    public async Task CreateAsync_ExplicitMode_OverridesAudienceDefaults()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.Temporary, Audience = FileShareAudience.Mcp },
            CancellationToken.None);

        Assert.Equal(FileShareMode.Temporary, link.Mode);
        Assert.Null(link.MaxDownloads);
        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 890, 900);
    }

    [Fact]
    public async Task CreateAsync_McpAudienceWithCustomExpiry_OverridesLifetimeOnly()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Audience = FileShareAudience.Mcp, ExpiresInSeconds = 600 },
            CancellationToken.None);

        Assert.Equal(2, link.MaxDownloads);
        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 590, 605);
    }

    [Fact]
    public async Task CreateAsync_SingleUseMode_LimitsToOneDownload()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.SingleUse },
            CancellationToken.None);

        Assert.Equal(1, link.MaxDownloads);
        FileShareLinkRecord record = Assert.Single(harness.Shares.Records.Values);
        Assert.Equal(1, record.MaxDownloads);
        Assert.Equal(asset.ObjectKey, record.ObjectKey);
        Assert.Equal("tenant-a", record.TenantId);
        string token = TokenFromUrl(link.Url);
        Assert.Equal(32, token.Length);
        Assert.Equal(64, record.ShareIdHash.Length);
        Assert.Equal(record.ShareIdHash, FileShareTokens.Hash(token));
    }

    [Fact]
    public async Task CreateAsync_LongTermMode_UsesLongLifetime()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.LongTerm },
            CancellationToken.None);

        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public async Task CreateAsync_CustomExpiry_OverridesModeDefault()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { ExpiresInSeconds = 3600 },
            CancellationToken.None);

        Assert.InRange((link.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 3590, 3600);
    }

    [Fact]
    public async Task CreateAsync_CustomExpiryOverLimit_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository, options: new FileShareOptions { MaxLifetimeSeconds = 600 });

        await Assert.ThrowsAsync<AgentException>(() => harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { ExpiresInSeconds = 3600 },
            CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task CreateAsync_NonPositiveExpiry_Rejects(int expiresInSeconds)
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        await Assert.ThrowsAsync<AgentException>(() => harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { ExpiresInSeconds = expiresInSeconds },
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_PersistsModeAndReturnsShareId()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.LongTerm },
            CancellationToken.None);

        FileShareLinkRecord record = Assert.Single(harness.Shares.Records.Values);
        Assert.Equal(FileShareMode.LongTerm, record.Mode);
        Assert.Equal(record.ShareIdHash, link.ShareId);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyOwnerShares_NewestFirst()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        FileAsset otherOwnerAsset = CreateAsset(fileId: "file-b", ownerUserId: "user-b");
        repository.Assets[asset.FileId] = asset;
        repository.Assets[otherOwnerAsset.FileId] = otherOwnerAsset;
        Harness harness = CreateHarness(repository);

        FileShareLink older = await harness.Service.CreateAsync(
            asset.FileId, Scope(), new FileShareRequest(), CancellationToken.None);
        FileShareLink newer = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.SingleUse },
            CancellationToken.None);
        await harness.Service.CreateAsync(
            otherOwnerAsset.FileId,
            new FileAssetScope { TenantId = "tenant-a", UserId = "user-b" },
            new FileShareRequest(),
            CancellationToken.None);
        // 拉开创建时间，验证按创建时间倒序。
        harness.Shares.Records[older.ShareId].CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        harness.Shares.Records[newer.ShareId].CreatedAt = DateTimeOffset.UtcNow;

        IReadOnlyList<FileShareSummary> summaries = await harness.Service.ListAsync(
            Scope(), CancellationToken.None);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(newer.ShareId, summaries[0].ShareId);
        Assert.Equal(FileShareMode.SingleUse, summaries[0].Mode);
        Assert.True(summaries[0].IsActive);
        Assert.Equal(older.ShareId, summaries[1].ShareId);
        Assert.True(summaries[1].IsActive);
    }

    [Fact]
    public async Task RevokeAsync_RemovesLinkAndKillsRedemption()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        objects.ContentsByKey[asset.ObjectKey] = "data"u8.ToArray();
        Harness harness = CreateHarness(repository, objects);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId, Scope(), new FileShareRequest(), CancellationToken.None);
        Assert.True(await harness.Service.RevokeAsync(link.ShareId, Scope(), CancellationToken.None));

        Assert.Null(await harness.Service.RedeemAsync(TokenFromUrl(link.Url), CancellationToken.None));
        Assert.Empty(await harness.Service.ListAsync(Scope(), CancellationToken.None));
        Assert.False(await harness.Service.RevokeAsync(link.ShareId, Scope(), CancellationToken.None));
    }

    [Theory]
    [InlineData("tenant-b", "user-a")]
    [InlineData("tenant-a", "user-b")]
    public async Task RevokeAsync_NotOwnerOrUnknown_ReturnsFalse(string tenantId, string userId)
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId, Scope(), new FileShareRequest(), CancellationToken.None);

        Assert.False(await harness.Service.RevokeAsync(
            link.ShareId,
            new FileAssetScope { TenantId = tenantId, UserId = userId },
            CancellationToken.None));
        Assert.False(await harness.Service.RevokeAsync(
            FileShareTokens.Hash(FileShareTokens.NewToken()),
            Scope(),
            CancellationToken.None));
        // 非本人撤销不影响原链接。
        Assert.True(harness.Shares.Records.ContainsKey(link.ShareId));
    }

    [Theory]
    [InlineData("tenant-b", "user-a")]
    [InlineData("tenant-a", "user-b")]
    public async Task CreateAsync_NotOwner_Rejects(string tenantId, string userId)
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        await Assert.ThrowsAsync<AgentException>(() => harness.Service.CreateAsync(
            asset.FileId,
            new FileAssetScope { TenantId = tenantId, UserId = userId },
            new FileShareRequest(),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_NotReadyAsset_Rejects()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset(state: FileAssetState.Pending);
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(repository);

        await Assert.ThrowsAsync<AgentException>(() => harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest(),
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_PublicBaseUrl_BuildsAbsoluteUrl()
    {
        var repository = new RecordingFileAssetRepository();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        Harness harness = CreateHarness(
            repository,
            options: new FileShareOptions { PublicBaseUrl = "https://engine.example.com/" });

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest(),
            CancellationToken.None);

        Assert.StartsWith(
            $"https://engine.example.com{IFileShareService.RoutePrefix}/",
            link.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedeemAsync_ValidToken_ReturnsFileContent()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        objects.ContentsByKey[asset.ObjectKey] = "# Report"u8.ToArray();
        Harness harness = CreateHarness(repository, objects);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest(),
            CancellationToken.None);
        FileShareRedemption? redemption = await harness.Service.RedeemAsync(
            TokenFromUrl(link.Url),
            CancellationToken.None);

        Assert.NotNull(redemption);
        Assert.Equal(asset.FileName, redemption!.FileName);
        Assert.Equal(asset.MediaType, redemption.MediaType);
        Assert.Equal("# Report", System.Text.Encoding.UTF8.GetString(redemption.Data));
    }

    [Fact]
    public async Task RedeemAsync_SingleUseLink_SecondAttemptReturnsNull()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        objects.ContentsByKey[asset.ObjectKey] = "data"u8.ToArray();
        Harness harness = CreateHarness(repository, objects);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest { Mode = FileShareMode.SingleUse },
            CancellationToken.None);
        string token = TokenFromUrl(link.Url);

        Assert.NotNull(await harness.Service.RedeemAsync(token, CancellationToken.None));
        Assert.Null(await harness.Service.RedeemAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task RedeemAsync_TemporaryLink_AllowsRepeatedDownloads()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        objects.ContentsByKey[asset.ObjectKey] = "data"u8.ToArray();
        Harness harness = CreateHarness(repository, objects);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest(),
            CancellationToken.None);
        string token = TokenFromUrl(link.Url);

        Assert.NotNull(await harness.Service.RedeemAsync(token, CancellationToken.None));
        Assert.NotNull(await harness.Service.RedeemAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task RedeemAsync_ExpiredLink_ReturnsNullWithoutReadingObject()
    {
        var repository = new RecordingFileAssetRepository();
        var objects = new RecordingFileObjectStore();
        FileAsset asset = CreateAsset();
        repository.Assets[asset.FileId] = asset;
        objects.ContentsByKey[asset.ObjectKey] = "data"u8.ToArray();
        Harness harness = CreateHarness(repository, objects);

        FileShareLink link = await harness.Service.CreateAsync(
            asset.FileId,
            Scope(),
            new FileShareRequest(),
            CancellationToken.None);
        string token = TokenFromUrl(link.Url);
        harness.Shares.Records[FileShareTokens.Hash(token)].ExpiresAt =
            DateTimeOffset.UtcNow.AddMinutes(-1);

        Assert.Null(await harness.Service.RedeemAsync(token, CancellationToken.None));
        Assert.Equal(0, objects.ReadCount);
    }

    [Fact]
    public async Task RedeemAsync_UnknownToken_ReturnsNull()
    {
        Harness harness = CreateHarness(new RecordingFileAssetRepository());

        Assert.Null(await harness.Service.RedeemAsync(FileShareTokens.NewToken(), CancellationToken.None));
    }

    private static string TokenFromUrl(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static FileAssetScope Scope() => new() { TenantId = "tenant-a", UserId = "user-a" };

    private static Harness CreateHarness(
        RecordingFileAssetRepository repository,
        RecordingFileObjectStore? objects = null,
        FileShareOptions? options = null)
    {
        RecordingFileObjectStore effectiveObjects = objects ?? new RecordingFileObjectStore();
        RecordingFileShareRepository shares = new();
        IFileShareService service = new FileShareService(
            new FileAssetService(
                repository,
                effectiveObjects,
                Options.Create(new FileAssetOptions
                {
                    Enabled = true,
                    MaxFileSizeBytes = 1024,
                    MaxFunctionReadBytes = 128
                })),
            shares,
            effectiveObjects,
            Options.Create(options ?? new FileShareOptions()));
        return new Harness(service, shares);
    }

    private static FileAsset CreateAsset(
        FileAssetState state = FileAssetState.Ready,
        string fileId = "file-a",
        string ownerUserId = "user-a") => new()
    {
        FileId = fileId,
        TenantId = "tenant-a",
        OwnerUserId = ownerUserId,
        FileName = "report.md",
        MediaType = "text/markdown",
        Length = 8,
        Sha256 = "sha",
        ObjectKey = $"files/tenants/{FileObjectTenantScope.CreatePartition("tenant-a")}/users/{ownerUserId}/{fileId}",
        Source = FileAssetSource.UserUpload,
        State = state,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed record Harness(IFileShareService Service, RecordingFileShareRepository Shares);
}
