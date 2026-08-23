using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OpenAgentDbContext))]
[Migration("20260819090000_AddConversationContextSummaries")]
public partial class AddConversationContextSummaries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ContextSummariesJson",
            schema: "openagent",
            table: "conversations",
            type: "jsonb",
            nullable: false,
            defaultValue: "[]");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ContextSummariesJson",
            schema: "openagent",
            table: "conversations");
    }
}
