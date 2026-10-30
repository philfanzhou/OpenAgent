using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class ResourcePolicyEntityConfiguration : IEntityTypeConfiguration<ResourcePolicyEntity>
{
    public void Configure(EntityTypeBuilder<ResourcePolicyEntity> entity)
    {
        entity.ToTable("resource_policies");
        entity.HasKey(item => new { item.TenantId, item.ResourceType, item.ResourceId });
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.ResourceType).HasConversion<string>().HasMaxLength(32);
        entity.Property(item => item.ResourceId).HasMaxLength(512);
        entity.Property(item => item.Revision).IsConcurrencyToken();
        entity.Property(item => item.RulesJson).HasColumnType("jsonb");
    }
}
