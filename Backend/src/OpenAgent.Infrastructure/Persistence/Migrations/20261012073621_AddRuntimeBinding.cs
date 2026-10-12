using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeJson",
                schema: "openagent",
                table: "agent_configurations",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "default", "development" },
                column: "RuntimeJson",
                value: "{}");

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "intent-router", "development" },
                column: "RuntimeJson",
                value: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeJson",
                schema: "openagent",
                table: "agent_configurations");
        }
    }
}
