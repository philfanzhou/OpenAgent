using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThirdPartyApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "third_party_api_keys",
                schema: "openagent",
                columns: table => new
                {
                    ApiKeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Scopes = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Roles = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Groups = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_third_party_api_keys", x => x.ApiKeyId);
                });

            migrationBuilder.InsertData(
                schema: "openagent",
                table: "third_party_api_keys",
                columns: new[] { "ApiKeyId", "CreatedAt", "Email", "ExpiresAt", "Groups", "IsEnabled", "KeyHash", "Name", "Roles", "Scopes", "TenantId", "UserId", "Username" },
                values: new object[,]
                {
                    { "demo-partner-a", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "", false, "1F0EDBABFE0BDAF41574D36AA8530D39233A8C832A5AF7B975E7784D6939C5A7", "Demo Partner A", "", "agent.execute model.invoke", "tenant-a", "integration:partner-a", "partner-a" },
                    { "demo-partner-b", new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), null, null, "", false, "46504FDCB1197B4268C79C2594C72B5FC02A0D03F7795F6B96B2B56386C0426F", "Demo Partner B", "", "agent.execute model.invoke", "tenant-b", "integration:partner-b", "partner-b" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_third_party_api_keys_KeyHash",
                schema: "openagent",
                table: "third_party_api_keys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_third_party_api_keys_TenantId_IsEnabled",
                schema: "openagent",
                table: "third_party_api_keys",
                columns: new[] { "TenantId", "IsEnabled" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "third_party_api_keys",
                schema: "openagent");
        }
    }
}
