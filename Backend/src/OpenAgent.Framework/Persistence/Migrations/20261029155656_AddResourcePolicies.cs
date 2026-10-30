using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Framework.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddResourcePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "resource_policies",
                schema: "openagent",
                columns: table => new
                {
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_policies", x => new { x.TenantId, x.ResourceType, x.ResourceId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_policies",
                schema: "openagent");
        }
    }
}
