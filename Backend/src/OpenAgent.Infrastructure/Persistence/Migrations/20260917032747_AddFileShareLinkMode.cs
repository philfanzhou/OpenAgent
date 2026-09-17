using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFileShareLinkMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_share_links_TenantId_CreatedAt",
                schema: "openagent",
                table: "file_share_links");

            migrationBuilder.AddColumn<int>(
                name: "Mode",
                schema: "openagent",
                table: "file_share_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 存量回填：单次分享历史上以 MaxDownloads=1 表达，补记 Mode。
            migrationBuilder.Sql(
                "UPDATE openagent.file_share_links SET \"Mode\" = 1 WHERE \"MaxDownloads\" = 1;");

            migrationBuilder.CreateIndex(
                name: "IX_file_share_links_TenantId_OwnerUserId_CreatedAt",
                schema: "openagent",
                table: "file_share_links",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_file_share_links_TenantId_OwnerUserId_CreatedAt",
                schema: "openagent",
                table: "file_share_links");

            migrationBuilder.DropColumn(
                name: "Mode",
                schema: "openagent",
                table: "file_share_links");

            migrationBuilder.CreateIndex(
                name: "IX_file_share_links_TenantId_CreatedAt",
                schema: "openagent",
                table: "file_share_links",
                columns: new[] { "TenantId", "CreatedAt" });
        }
    }
}
