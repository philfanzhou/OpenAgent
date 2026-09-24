using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenAgent.Infrastructure;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(OpenAgentDbContext))]
[Migration("20260924000100_AddAgentSessionSnapshot")]
public partial class AddAgentSessionSnapshot : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AgentSessionConfigFingerprint",
            schema: "openagent",
            table: "conversations",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AgentSessionFormatVersion",
            schema: "openagent",
            table: "conversations",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "AgentSessionStateJson",
            schema: "openagent",
            table: "conversations",
            type: "jsonb",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AgentSessionConfigFingerprint",
            schema: "openagent",
            table: "conversations");

        migrationBuilder.DropColumn(
            name: "AgentSessionFormatVersion",
            schema: "openagent",
            table: "conversations");

        migrationBuilder.DropColumn(
            name: "AgentSessionStateJson",
            schema: "openagent",
            table: "conversations");
    }
}
