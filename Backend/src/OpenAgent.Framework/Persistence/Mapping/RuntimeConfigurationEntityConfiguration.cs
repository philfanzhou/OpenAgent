using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class RuntimeConfigurationEntityConfiguration : IEntityTypeConfiguration<RuntimeConfigurationEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeConfigurationEntity> entity)
    {
        entity.ToTable("runtime_configurations");
        entity.HasKey(item => new { item.TenantId, item.RuntimeId });
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.RuntimeId).HasMaxLength(256);
        entity.Property(item => item.DefinitionJson).HasColumnType("jsonb");
    }
}
