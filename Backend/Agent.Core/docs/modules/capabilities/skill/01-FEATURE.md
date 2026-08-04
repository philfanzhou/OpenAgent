# Feature: 技能发现与执行

## 用户故事

作为执行内核，我希望模型可调用的执行能力被统一发现、过滤和执行，以便不同来源的能力在工具调用层面保持一致的语义。

## 概述

Skill 是 Agent.Core 中模型可调用的执行能力之一，用于承载明确的动作操作，而非知识检索。技能来源分为本地注册（ISkill/IToolRegistry）、MCP 外部工具和动态注册三类。

## 核心能力

- 按 AgentConfig.Skills 列表过滤可用技能
- 基于用户上下文的 ACL 权限过滤
- 统一收集为 SkillDescriptor 列表供引擎使用
- 按优先级路由执行：ToolRegistry → ISkillService → MCP

## 来源分层

| 来源 | SkillSource 枚举值 | 说明 |
|------|-------------------|------|
| 本地 Skill | `Local` | 运行时直接注册的工具能力（IToolRegistry）和本地 ISkill 实现 |
| MCP 外部工具 | `Mcp` | 通过 MCP 协议暴露的外部能力 |
| Matrix 平台 | `Matrix` | 运行时动态注入的外部平台能力 |

## 核心流程

1. **收集**：从 ToolRegistry、ISkillService、IMcpClient、动态注册四个渠道收集 SkillDescriptor
2. **配置过滤**：根据 SkillsConfig 过滤启用的 Skill
3. **权限过滤**：根据 IAgentUserContext 过滤可见 Skill
4. **暴露**：过滤后的 SkillDescriptor 转为 ToolDefinition 供引擎使用
5. **执行**：模型发起 Tool Call 后，由 SkillProvider 路由到对应执行器

## 当前状态

**已实现** — 技能发现、过滤与执行链路均已落地。

## 当前限制

- MCP 描述符收集使用同步 `.GetAwaiter().GetResult()`，在高并发场景下可能阻塞
- 无技能调用配额控制（SkillQuotaExceeded 错误码已定义但未使用）
- 无技能参数验证链路（SkillValidationFailed 错误码已定义但未使用）

## 相关文档

- [02-ARCHITECTURE](./02-ARCHITECTURE.md)
- [03-DATA-MODELS](./03-DATA-MODELS.md)
- [04-API](./04-API.md)
- [05-TESTING](./05-TESTING.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
