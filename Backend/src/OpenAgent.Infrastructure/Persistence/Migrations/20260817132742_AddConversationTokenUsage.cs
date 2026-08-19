using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CachedInputTokens",
                schema: "openagent",
                table: "conversation_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                schema: "openagent",
                table: "conversation_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                schema: "openagent",
                table: "conversation_messages",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                schema: "openagent",
                table: "conversation_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReasoningTokens",
                schema: "openagent",
                table: "conversation_messages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTokens",
                schema: "openagent",
                table: "conversation_messages",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CachedInputTokens",
                schema: "openagent",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                schema: "openagent",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "ModelId",
                schema: "openagent",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                schema: "openagent",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "ReasoningTokens",
                schema: "openagent",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "TotalTokens",
                schema: "openagent",
                table: "conversation_messages");
        }
    }
}
