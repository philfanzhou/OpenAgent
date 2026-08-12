using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOpenAgentPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "openagent");

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "openagent",
                columns: table => new
                {
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MessageCount = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IsDeletedByUser = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.ConversationId);
                });

            migrationBuilder.CreateTable(
                name: "file_assets",
                schema: "openagent",
                columns: table => new
                {
                    FileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FileName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_file_assets", x => x.FileId);
                });

            migrationBuilder.CreateTable(
                name: "conversation_messages",
                schema: "openagent",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ToolCallId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ToolName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_messages", x => x.MessageId);
                    table.ForeignKey(
                        name: "FK_conversation_messages_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "openagent",
                        principalTable: "conversations",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conversation_file_references",
                schema: "openagent",
                columns: table => new
                {
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_file_references", x => new { x.ConversationId, x.FileId });
                    table.ForeignKey(
                        name: "FK_conversation_file_references_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "openagent",
                        principalTable: "conversations",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_conversation_file_references_file_assets_FileId",
                        column: x => x.FileId,
                        principalSchema: "openagent",
                        principalTable: "file_assets",
                        principalColumn: "FileId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "message_file_references",
                schema: "openagent",
                columns: table => new
                {
                    MessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_file_references", x => new { x.MessageId, x.FileId });
                    table.ForeignKey(
                        name: "FK_message_file_references_conversation_messages_MessageId",
                        column: x => x.MessageId,
                        principalSchema: "openagent",
                        principalTable: "conversation_messages",
                        principalColumn: "MessageId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_message_file_references_file_assets_FileId",
                        column: x => x.FileId,
                        principalSchema: "openagent",
                        principalTable: "file_assets",
                        principalColumn: "FileId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conversation_file_references_FileId",
                schema: "openagent",
                table: "conversation_file_references",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_messages_ConversationId_Sequence",
                schema: "openagent",
                table: "conversation_messages",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_IsDeletedByUser_LastMessageAt",
                schema: "openagent",
                table: "conversations",
                columns: new[] { "TenantId", "IsDeletedByUser", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_TenantId_LastMessageAt",
                schema: "openagent",
                table: "conversations",
                columns: new[] { "TenantId", "LastMessageAt" });

            migrationBuilder.CreateIndex(
                name: "IX_file_assets_TenantId_OwnerUserId_CreatedAt",
                schema: "openagent",
                table: "file_assets",
                columns: new[] { "TenantId", "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_message_file_references_FileId",
                schema: "openagent",
                table: "message_file_references",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_file_references",
                schema: "openagent");

            migrationBuilder.DropTable(
                name: "message_file_references",
                schema: "openagent");

            migrationBuilder.DropTable(
                name: "conversation_messages",
                schema: "openagent");

            migrationBuilder.DropTable(
                name: "file_assets",
                schema: "openagent");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "openagent");
        }
    }
}
