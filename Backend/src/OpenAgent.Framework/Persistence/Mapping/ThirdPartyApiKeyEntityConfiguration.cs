using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class ThirdPartyApiKeyEntityConfiguration : IEntityTypeConfiguration<ThirdPartyApiKeyEntity>
{
    public void Configure(EntityTypeBuilder<ThirdPartyApiKeyEntity> entity)
    {
        entity.ToTable("third_party_api_keys");
        entity.HasKey(item => item.ApiKeyId);
        entity.Property(item => item.ApiKeyId).HasMaxLength(64);
        entity.Property(item => item.Name).HasMaxLength(256);
        entity.Property(item => item.KeyHash).HasMaxLength(64);
        entity.Property(item => item.UserId).HasMaxLength(256);
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.Username).HasMaxLength(256);
        entity.Property(item => item.Email).HasMaxLength(512);
        entity.Property(item => item.Scopes).HasMaxLength(2048);
        entity.Property(item => item.Roles).HasMaxLength(2048);
        entity.Property(item => item.Groups).HasMaxLength(2048);
        entity.HasIndex(item => item.KeyHash).IsUnique();
        entity.HasIndex(item => new { item.TenantId, item.IsEnabled });
        entity.HasData(
            new ThirdPartyApiKeyEntity
            {
                ApiKeyId = "demo-partner-a",
                Name = "Demo Partner A",
                KeyHash = "1F0EDBABFE0BDAF41574D36AA8530D39233A8C832A5AF7B975E7784D6939C5A7",
                UserId = "integration:partner-a",
                TenantId = "tenant-a",
                Username = "partner-a",
                Scopes = "agent.execute model.invoke",
                IsEnabled = false,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            },
            new ThirdPartyApiKeyEntity
            {
                ApiKeyId = "demo-partner-b",
                Name = "Demo Partner B",
                KeyHash = "46504FDCB1197B4268C79C2594C72B5FC02A0D03F7795F6B96B2B56386C0426F",
                UserId = "integration:partner-b",
                TenantId = "tenant-b",
                Username = "partner-b",
                Scopes = "agent.execute model.invoke",
                IsEnabled = false,
                CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            });
    }
}
