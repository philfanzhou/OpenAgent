using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHumanApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "human_approvals",
                schema: "openagent",
                columns: table => new
                {
                    ApprovalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TraceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    TargetCapability = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RedactedArgumentsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionReason = table.Column<string>(type: "text", nullable: true),
                    MafRequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToolCallId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionStateJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequesterContextJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_human_approvals", x => x.ApprovalId);
                    table.ForeignKey(
                        name: "FK_human_approvals_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "openagent",
                        principalTable: "conversations",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_human_approvals_ConversationId",
                schema: "openagent",
                table: "human_approvals",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_human_approvals_TenantId_ConversationId",
                schema: "openagent",
                table: "human_approvals",
                columns: new[] { "TenantId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_human_approvals_TenantId_Status_ExpiresAt",
                schema: "openagent",
                table: "human_approvals",
                columns: new[] { "TenantId", "Status", "ExpiresAt" });

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "human_approvals",
                schema: "openagent");
        }
    }
}
