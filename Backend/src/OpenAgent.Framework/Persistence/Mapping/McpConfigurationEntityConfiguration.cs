using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class McpConfigurationEntityConfiguration : IEntityTypeConfiguration<McpConfigurationEntity>
{
    public void Configure(EntityTypeBuilder<McpConfigurationEntity> entity)
    {
        entity.ToTable("mcp_configurations");
        entity.HasKey(item => new { item.TenantId, item.Name });
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.Name).HasMaxLength(256);
        entity.Property(item => item.DefinitionJson).HasColumnType("jsonb");
    }
}
