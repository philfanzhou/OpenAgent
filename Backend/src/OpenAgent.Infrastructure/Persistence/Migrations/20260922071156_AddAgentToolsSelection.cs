using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentToolsSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ToolsJson",
                schema: "openagent",
                table: "agent_configurations",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "default", "development" },
                column: "ToolsJson",
                value: "{}");

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "intent-router", "development" },
                column: "ToolsJson",
                value: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ToolsJson",
                schema: "openagent",
                table: "agent_configurations");
        }
    }
}
