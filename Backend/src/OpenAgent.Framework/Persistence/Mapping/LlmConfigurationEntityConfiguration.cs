using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class LlmConfigurationEntityConfiguration : IEntityTypeConfiguration<LlmConfigurationEntity>
{
    public void Configure(EntityTypeBuilder<LlmConfigurationEntity> entity)
    {
        entity.ToTable("llm_configurations");
        entity.HasKey(item => new { item.TenantId, item.ProfileId });
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.ProfileId).HasMaxLength(256);
        entity.Property(item => item.Format).HasConversion<string>().HasMaxLength(32);
        entity.Property(item => item.Modality).HasConversion<string>().HasMaxLength(32);
        entity.HasIndex(item => new { item.TenantId, item.UpdatedAt });
    }
}
