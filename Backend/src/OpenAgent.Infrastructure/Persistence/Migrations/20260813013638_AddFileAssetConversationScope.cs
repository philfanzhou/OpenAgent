using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFileAssetConversationScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_assets_TenantId_OwnerUserId_CreatedAt",
                schema: "openagent",
                table: "file_assets");

            migrationBuilder.AddColumn<string>(
                name: "ConversationId",
                schema: "openagent",
                table: "file_assets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                UPDATE openagent.file_assets AS assets
                SET "ConversationId" = scoped_files."ConversationId"
                FROM (
                    SELECT "FileId", MIN("ConversationId") AS "ConversationId"
                    FROM openagent.conversation_file_references
                    GROUP BY "FileId"
                    HAVING COUNT(DISTINCT "ConversationId") = 1
                ) AS scoped_files
                WHERE assets."FileId" = scoped_files."FileId";

                ALTER TABLE openagent.file_assets
                    ALTER COLUMN "ConversationId" DROP DEFAULT;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_file_assets_TenantId_OwnerUserId_ConversationId_CreatedAt",
                schema: "openagent",
                table: "file_assets",
                columns: new[] { "TenantId", "OwnerUserId", "ConversationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_assets_TenantId_OwnerUserId_ConversationId_CreatedAt",
                schema: "openagent",
                table: "file_assets");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                schema: "openagent",
                table: "file_assets");

            migrationBuilder.CreateIndex(
                name: "IX_file_assets_TenantId_OwnerUserId_CreatedAt",
                schema: "openagent",
                table: "file_assets",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });
        }
    }
}
