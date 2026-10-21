using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Framework.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeProfilesAndDurableLocalConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OpenCodeJson",
                schema: "openagent",
                table: "agent_configurations",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "mcp_configurations",
                schema: "openagent",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mcp_configurations", x => new { x.TenantId, x.Name });
                });

            migrationBuilder.CreateTable(
                name: "runtime_configurations",
                schema: "openagent",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RuntimeId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_configurations", x => new { x.TenantId, x.RuntimeId });
                });

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "default", "development" },
                column: "OpenCodeJson",
                value: "{}");

            migrationBuilder.UpdateData(
                schema: "openagent",
                table: "agent_configurations",
                keyColumns: new[] { "AgentId", "TenantId" },
                keyValues: new object[] { "intent-router", "development" },
                column: "OpenCodeJson",
                value: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_configurations",
                schema: "openagent");

            migrationBuilder.DropTable(
                name: "runtime_configurations",
                schema: "openagent");

            migrationBuilder.DropColumn(
                name: "OpenCodeJson",
                schema: "openagent",
                table: "agent_configurations");
        }
    }
}
