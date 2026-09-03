using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenAgent.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OpenAgentDbContext))]
[Migration("20260903100000_AddModelTokenLimits")]
public sealed class AddModelTokenLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ContextWindowTokens", schema: "openagent", table: "agent_configurations",
            type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "MaxOutputTokens", schema: "openagent", table: "agent_configurations",
            type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "MaxOutputTokens", schema: "openagent", table: "llm_configurations",
            type: "integer", nullable: true);
        migrationBuilder.AddColumn<bool>(
            name: "SupportsMaxOutputTokens", schema: "openagent", table: "llm_configurations",
            type: "boolean", nullable: false, defaultValue: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("ContextWindowTokens", "agent_configurations", "openagent");
        migrationBuilder.DropColumn("MaxOutputTokens", "agent_configurations", "openagent");
        migrationBuilder.DropColumn("MaxOutputTokens", "llm_configurations", "openagent");
        migrationBuilder.DropColumn("SupportsMaxOutputTokens", "llm_configurations", "openagent");
    }
}
