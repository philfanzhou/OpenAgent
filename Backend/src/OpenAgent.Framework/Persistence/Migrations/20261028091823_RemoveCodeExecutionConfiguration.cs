using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Framework.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCodeExecutionConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeExecutionJson",
                schema: "openagent",
                table: "agent_configurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeExecutionJson",
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
                column: "CodeExecutionJson",
                value: "{}");

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "intent-router", "development" },
                column: "CodeExecutionJson",
                value: "{}");
        }
    }
}
