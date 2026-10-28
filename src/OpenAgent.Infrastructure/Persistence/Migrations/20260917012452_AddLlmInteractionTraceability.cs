using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmInteractionTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                schema: "openagent",
                table: "conversation_messages",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "llm_interaction_logs",
                schema: "openagent",
                columns: table => new
                {
                    InteractionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ApiFormat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Streamed = table.Column<bool>(type: "boolean", nullable: false),
                    CallIndex = table.Column<int>(type: "integer", nullable: false),
                    RequestJson = table.Column<string>(type: "jsonb", nullable: true),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: true),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    CachedInputTokens = table.Column<int>(type: "integer", nullable: true),
                    ReasoningTokens = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_llm_interaction_logs", x => x.InteractionId);
                    table.ForeignKey(
                        name: "FK_llm_interaction_logs_conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "openagent",
                        principalTable: "conversations",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_llm_interaction_logs_ConversationId",
                schema: "openagent",
                table: "llm_interaction_logs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_llm_interaction_logs_TenantId_ConversationId_StartedAt",
                schema: "openagent",
                table: "llm_interaction_logs",
                columns: new[] { "TenantId", "ConversationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_llm_interaction_logs_TraceId",
                schema: "openagent",
                table: "llm_interaction_logs",
                column: "TraceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_interaction_logs",
                schema: "openagent");

            migrationBuilder.DropColumn(
                name: "TraceId",
                schema: "openagent",
                table: "conversation_messages");
        }
    }
}
