using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OpenAgentDbContext))]
[Migration("20260902160000_AddLlmConfigurations")]
public sealed class AddLlmConfigurations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "llm_configurations",
            schema: "openagent",
            columns: table => new
            {
                TenantId = table.Column<string>(
                    type: "character varying(256)",
                    maxLength: 256,
                    nullable: false),
                ProfileId = table.Column<string>(
                    type: "character varying(256)",
                    maxLength: 256,
                    nullable: false),
                ConfigurationJson = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_llm_configurations", x => new { x.TenantId, x.ProfileId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_llm_configurations_TenantId_UpdatedAt",
            schema: "openagent",
            table: "llm_configurations",
            columns: new[] { "TenantId", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "llm_configurations",
            schema: "openagent");
    }
}
