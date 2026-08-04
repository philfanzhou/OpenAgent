# 添加 Agent 技能

## 用途
引导式工作流：向 OpenAgent 平台添加新的 Agent 技能（Skill）。

## 触发条件
- 用户要求"添加技能"、"新建 Skill"、"增加 Agent 能力"
- 需要让 Agent 执行特定的业务逻辑或组合操作

## 输入参数
无（交互式收集需求）

---

## 工作流程

### 第一阶段：理解需求

向用户确认以下信息：
1. 这个技能做什么？（一句话描述）
2. 是**简单技能**（纯计算/处理，不调 MCP）还是**业务技能**（需要调用 MCP 工具组合查询）？
3. 需要哪些输入参数？
4. 输出什么？

### 第二阶段：学习现有模式

在动手写代码前，先阅读以下参考文件：

**技能系统架构：**
- `docs/modules/capabilities/skill/` — 技能能力文档
- `Agent.Core/src/Core/` — ISkillService、ISkillProvider 接口

**现有技能实现（最佳参考）：**

简单技能（纯逻辑，不调外部服务）：
- `TestCode/Agent.TestSkillService/Skills/Simple/CalculatorSkill.cs` — 算术计算
- `TestCode/Agent.TestSkillService/Skills/Simple/TextProcessorSkill.cs` — 文本处理
- `TestCode/Agent.TestSkillService/Skills/Simple/PasswordGeneratorSkill.cs` — 密码生成

业务技能（调用 MCP 工具组合）：
- `TestCode/Agent.TestSkillService/Skills/Business/Employee360Skill.cs` — 跨 3 个数据库的员工全景（HR+Finance+IT）
- `TestCode/Agent.TestSkillService/Skills/Business/ExpenseQuickApproveSkill.cs` — 费用审批（Finance+HR）
- `TestCode/Agent.TestSkillService/Skills/Business/AssetReassignmentSkill.cs` — 资产重新分配（IT+HR）

**技能基类：**
- `TestCode/Agent.TestSkillService/SkillBase.cs` — 所有技能的抽象基类
- `TestCode/Agent.TestSkillService/SkillRegistry.cs` — 技能注册

**测试指南：**
- `TestCode/scripts/integration/test-it-skill-mcp-integration.ps1` — Skill-MCP 集成测试

### 第三阶段：实现

#### 步骤 1: 创建技能类

在 `TestCode/Agent.TestSkillService/Skills/` 下创建：
- 简单技能 → `Simple/<Name>Skill.cs`
- 业务技能 → `Business/<Name>Skill.cs`

遵循 SkillBase 的模板：
```csharp
public class MyNewSkill : SkillBase
{
    public override string Name => "my-new-skill";
    public override string Description => "技能描述";

    public override async Task<SkillResult> ExecuteAsync(SkillRequest request)
    {
        // 实现技能逻辑
        // 业务技能通过 McpClient 调用 MCP 工具
    }
}
```

#### 步骤 2: 注册技能

在 `TestCode/Agent.TestSkillService/SkillRegistry.cs` 中注册新技能。

#### 步骤 3: 创建技能定义

通过 Engine API 创建 Skill 注册，JSON 格式参考：
```json
{
  "SkillId": "my-new-skill",
  "Name": "我的新技能",
  "Description": "...",
  "Parameters": [...],
  "Endpoint": "http://localhost:8091/skills/my-new-skill"
}
```

#### 步骤 4: 给 Agent 绑定技能

在对应 Agent 配置 JSON 的 `Skills.EnabledSkills` 中添加 `"my-new-skill"`：
```json
"Skills": {
  "EnabledSkills": ["query-leave", "my-new-skill"]
}
```

> ⚠️ 这些 JSON 会被 `Update-AgentConfigs` 重写。

### 第四阶段：验证

1. 构建：
   ```powershell
   dotnet build TestCode/TestEnv.sln
   ```
2. 启动服务：
   ```powershell
   cd TestCode/scripts
   ./start-services.ps1
   ```
3. 测试技能对比（有技能 vs 无技能）：
   ```powershell
   cd TestCode/scripts
   ./test-e2e.ps1 --skip-llm
   ```

### 第五阶段：更新文档

- `docs/modules/capabilities/skill/` — 如果新增技能模式
- `AGENTS.md` — 如果约定变化

---

## 参考文件
- 技能基类：`TestCode/Agent.TestSkillService/SkillBase.cs`
- 技能注册：`TestCode/Agent.TestSkillService/SkillRegistry.cs`
- 简单技能模板：`TestCode/Agent.TestSkillService/Skills/Simple/CalculatorSkill.cs`
- 业务技能模板：`TestCode/Agent.TestSkillService/Skills/Business/Employee360Skill.cs`

## 验证方法
- 新技能可通过 `/skills/list` 端点发现
- 调用 `/skills/execute` 返回正确结果
- Agent 在有 Skill 的情况下能正确调用
