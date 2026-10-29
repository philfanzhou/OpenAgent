using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenAgent.Framework.Entities;

namespace OpenAgent.Framework.Persistence.Mapping;

internal sealed class SkillDefinitionEntityConfiguration : IEntityTypeConfiguration<SkillDefinitionEntity>
{
    public void Configure(EntityTypeBuilder<SkillDefinitionEntity> entity)
    {
        entity.ToTable("skill_definitions");
        entity.HasKey(item => new { item.TenantId, item.SkillId, item.Type });
        entity.Property(item => item.TenantId).HasMaxLength(256);
        entity.Property(item => item.SkillId).HasMaxLength(256);
        entity.Property(item => item.Type).HasMaxLength(64);
        entity.Property(item => item.SourceType).HasMaxLength(64);
        entity.Property(item => item.DefinitionJson).HasColumnType("jsonb");
        entity.HasIndex(item => new { item.TenantId, item.UpdatedAt });
    }
}
