using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTypeAndOwnerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerRole",
                schema: "openagent",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                schema: "openagent",
                table: "conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerRole",
                schema: "openagent",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "Type",
                schema: "openagent",
                table: "conversations");
        }
    }
}
