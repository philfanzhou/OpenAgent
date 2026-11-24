using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Framework.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeOutputPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RuntimeOutputPath",
                schema: "openagent",
                table: "file_assets",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RuntimeOutputPath",
                schema: "openagent",
                table: "file_assets");
        }
    }
}
