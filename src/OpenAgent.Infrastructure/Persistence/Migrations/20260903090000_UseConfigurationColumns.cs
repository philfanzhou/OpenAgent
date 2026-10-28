using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenAgent.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OpenAgentDbContext))]
[Migration("20260903090000_UseConfigurationColumns")]
public sealed class UseConfigurationColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Name", schema: "openagent", table: "agent_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Description", schema: "openagent", table: "agent_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Status", schema: "openagent", table: "agent_configurations",
            type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Draft",
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Instructions", schema: "openagent", table: "agent_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<int>(
            name: "MaxTurns", schema: "openagent", table: "agent_configurations",
            type: "integer", nullable: false, defaultValue: 50,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "ContextPolicyJson", schema: "openagent", table: "agent_configurations",
            type: "jsonb", nullable: true,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "McpJson", schema: "openagent", table: "agent_configurations",
            type: "jsonb", nullable: false, defaultValue: "{}",
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "RagJson", schema: "openagent", table: "agent_configurations",
            type: "jsonb", nullable: false, defaultValue: "{}",
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "SkillsJson", schema: "openagent", table: "agent_configurations",
            type: "jsonb", nullable: false, defaultValue: "{}",
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Name", schema: "openagent", table: "llm_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Format", schema: "openagent", table: "llm_configurations",
            type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "OpenAIChatCompletions",
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "ModelId", schema: "openagent", table: "llm_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Endpoint", schema: "openagent", table: "llm_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "ApiKey", schema: "openagent", table: "llm_configurations",
            type: "text", nullable: false, defaultValue: string.Empty,
            comment: null);
        migrationBuilder.AddColumn<double>(
            name: "Temperature", schema: "openagent", table: "llm_configurations",
            type: "double precision", nullable: false, defaultValue: 0.7,
            comment: null);
        migrationBuilder.AddColumn<int>(
            name: "ContextTokens", schema: "openagent", table: "llm_configurations",
            type: "integer", nullable: false, defaultValue: 0,
            comment: null);
        migrationBuilder.AddColumn<string>(
            name: "Modality", schema: "openagent", table: "llm_configurations",
            type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Text",
            comment: null);

        migrationBuilder.Sql("""
            UPDATE openagent.agent_configurations SET
                "Name" = COALESCE("ConfigurationJson" ->> 'Name', ''),
                "Description" = COALESCE("ConfigurationJson" ->> 'Description', ''),
                "Status" = CASE "ConfigurationJson" ->> 'Status'
                    WHEN '0' THEN 'Draft' WHEN '1' THEN 'PendingReview'
                    WHEN '2' THEN 'Published' WHEN 'Snapshot' THEN 'Published'
                    ELSE COALESCE("ConfigurationJson" ->> 'Status', 'Draft') END,
                "Instructions" = COALESCE("ConfigurationJson" -> 'Config' ->> 'Instructions', ''),
                "MaxTurns" = COALESCE(("ConfigurationJson" -> 'Config' ->> 'MaxTurns')::integer, 50),
                "ContextPolicyJson" = NULLIF("ConfigurationJson" -> 'Config' -> 'ContextPolicy', 'null'::jsonb),
                "McpJson" = COALESCE(NULLIF("ConfigurationJson" -> 'Config' -> 'Mcp', 'null'::jsonb), '{}'::jsonb),
                "RagJson" = COALESCE(NULLIF("ConfigurationJson" -> 'Config' -> 'Rag', 'null'::jsonb), '{}'::jsonb),
                "SkillsJson" = COALESCE(NULLIF("ConfigurationJson" -> 'Config' -> 'Skills', 'null'::jsonb), '{}'::jsonb);

            UPDATE openagent.llm_configurations SET
                "Name" = COALESCE("ConfigurationJson" ->> 'Name', ''),
                "Format" = CASE "ConfigurationJson" ->> 'Format'
                    WHEN '0' THEN 'OpenAIChatCompletions' WHEN '1' THEN 'OpenAIResponses'
                    WHEN '2' THEN 'AnthropicMessages'
                    ELSE COALESCE("ConfigurationJson" ->> 'Format', 'OpenAIChatCompletions') END,
                "ModelId" = COALESCE("ConfigurationJson" ->> 'ModelId', ''),
                "Endpoint" = COALESCE("ConfigurationJson" ->> 'Endpoint', ''),
                "ApiKey" = COALESCE("ConfigurationJson" ->> 'ApiKey', ''),
                "Temperature" = COALESCE(("ConfigurationJson" ->> 'Temperature')::double precision, 0.7),
                "ContextTokens" = COALESCE(("ConfigurationJson" ->> 'ContextTokens')::integer,
                    ("ConfigurationJson" ->> 'ContextWindowTokens')::integer, 0),
                "Modality" = CASE "ConfigurationJson" ->> 'Modality'
                    WHEN '0' THEN 'Text' WHEN '1' THEN 'Multimodal'
                    ELSE COALESCE("ConfigurationJson" ->> 'Modality', 'Text') END;
            """);

        migrationBuilder.DropColumn(name: "ConfigurationJson", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "ConfigurationJson", schema: "openagent", table: "llm_configurations");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ConfigurationJson", schema: "openagent", table: "agent_configurations",
            type: "jsonb", nullable: false, defaultValue: "{}");
        migrationBuilder.AddColumn<string>(
            name: "ConfigurationJson", schema: "openagent", table: "llm_configurations",
            type: "jsonb", nullable: false, defaultValue: "{}");
        migrationBuilder.Sql("""
            UPDATE openagent.agent_configurations SET "ConfigurationJson" = jsonb_build_object(
                'TenantId', "TenantId", 'AgentId', "AgentId", 'Name', "Name", 'Description', "Description",
                'Status', "Status", 'CurrentVersion', "Version"::text,
                'Config', jsonb_build_object(
                    'TenantId', "TenantId", 'Instructions', "Instructions", 'MaxTurns', "MaxTurns",
                    'ContextPolicy', "ContextPolicyJson", 'Mcp', "McpJson", 'Rag', "RagJson", 'Skills', "SkillsJson"));
            UPDATE openagent.llm_configurations SET "ConfigurationJson" = jsonb_build_object(
                'TenantId', "TenantId", 'Id', "ProfileId", 'Name', "Name", 'Format', "Format",
                'ModelId', "ModelId", 'Endpoint', "Endpoint", 'ApiKey', "ApiKey",
                'Temperature', "Temperature", 'ContextWindowTokens', "ContextTokens", 'Modality', "Modality");
            """);
        migrationBuilder.DropColumn(name: "Name", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "Description", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "Status", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "Instructions", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "MaxTurns", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "ContextPolicyJson", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "McpJson", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "RagJson", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "SkillsJson", schema: "openagent", table: "agent_configurations");
        migrationBuilder.DropColumn(name: "Name", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "Format", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "ModelId", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "Endpoint", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "ApiKey", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "Temperature", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "ContextTokens", schema: "openagent", table: "llm_configurations");
        migrationBuilder.DropColumn(name: "Modality", schema: "openagent", table: "llm_configurations");
    }
}
