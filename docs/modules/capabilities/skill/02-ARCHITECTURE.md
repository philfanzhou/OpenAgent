# Architecture: 技能发现与执行

## 收集流程

SkillProvider.GetSkillDescriptorsAsync 按以下顺序收集描述符：

1. **ToolRegistry 描述符**：遍历 IToolRegistry.GetTools()，每个 ToolDefinition 转为 SkillDescriptor（Source=Local）
2. **本地 Skill 描述符**：遍历 ISkillService.GetSkills()（运行时注册的 `_registeredSkills`），跳过已存在的同名项，转为 SkillDescriptor（Source=Local）
3. **MCP 描述符**：若 IMcpClient.IsConnected，调用 ListToolsAsync()，跳过已存在的同名项，转为 SkillDescriptor（Source=Mcp）
4. **动态描述符**：遍历运行时通过 RegisterSkill/RegisterMcpSkills 注册的描述符，跳过已存在的同名项

同名判断：Name 字段忽略大小写比较（StringComparison.OrdinalIgnoreCase）。

## 配置过滤

FilterByConfig 逻辑：

1. 若 SkillsConfig 为 null，不过滤，返回全部
2. 若 Instances 列表非空，取 Enabled=true 的实例，按 Name/Id 匹配描述符
3. 若 EnabledSkills 列表非空，按 Name 匹配描述符
4. 若配置存在但 Instances 和 EnabledSkills 均为空数组，返回空列表（不启用任何 Skill）

## 权限过滤

FilterByPermission 逻辑（IsAllowedForUser 静态方法）：

1. 若描述符的四个 ACL 列表均为空，允许所有用户访问
2. 若 userContext 为 null 且 ACL 列表非空，拒绝访问
3. 依次检查 AllowedUserIds、AllowedGroups、AllowedTenantIds、AllowedRoles，任一匹配即允许（OR 语义）

## 执行路由

SkillProvider.ExecuteAsync 按以下优先级路由：

1. 若 IToolRegistry.HasTool(name) 为 true → IToolRegistry.ExecuteToolAsync
2. 若 ISkillService 中存在同名 Skill → ISkillService.ExecuteSkillAsync
3. 否则 → IMcpClient.CallToolAsync

## 注册行为

- SkillService 是精简的注册容器：构造函数仅依赖 `ILogger<SkillService>`，不再注入 `IServiceProvider` / `IEnumerable<ISkill>`
- SkillService.RegisterSkill(ISkill) 检查名称重复（忽略大小写），重复时跳过并记录 Warning 日志
- SkillService.GetSkills 返回 `_registeredSkills`（不再拼接 DI 注入的 `_injectedSkills`）
- SkillProvider.RegisterSkill 同时注册到 ISkillService 和动态描述符列表
- SkillProvider.RegisterMcpSkills 为每个 MCP 工具创建描述符，Id 格式为 `{serverUrl}:{toolName}`
- 已移除的能力：`RegisterSkill<T>` 泛型注册、`ActivatorUtilities.CreateInstance`、执行时创建 DI scope 获取 scoped ISkill

## SkillService 执行细节

ExecuteSkillAsync 中：
- 直接调用 `skill.ExecuteAsync`，不再创建 DI scope 获取 scoped 服务
- 执行异常时捕获并返回 `"Error: {ex.Message}"` 字符串（不抛出）

## 错误处理

### 错误码

与 Skill 相关的 AgentErrorCode：

| 错误码 | 值 | 说明 |
|--------|-----|------|
| `UnauthorizedSkill` | 1001 | 用户无权访问该技能 |
| `SkillNotFound` | 1002 | 技能未找到 |
| `SkillExecutionFailed` | 1003 | 技能执行失败 |
| `SkillTimeout` | 1004 | 技能执行超时 |
| `SkillValidationFailed` | 1005 | 技能参数验证失败 |
| `SkillQuotaExceeded` | 1006 | 技能调用配额超限 |

### 异常类型

**ToolExecutionException** — 工具执行失败异常，继承自 AgentException。

| 属性 | 说明 |
|------|------|
| `ToolName` | 失败的工具名称 |
| `Arguments` | 调用参数 |
| `ErrorCode` | 固定为 SkillExecutionFailed (1003) |

**HumanApprovalRequiredException** — 需要人工审批异常。

| 属性 | 说明 |
|------|------|
| `ActionDescription` | 待审批动作描述 |
| `ApprovalToken` | 审批令牌 |
| `ErrorCode` | 固定为 HumanApprovalRequired (7001) |

### 运行时错误处理

- SkillService.ExecuteSkillAsync：技能未找到 → 抛出 `ArgumentException`；执行异常 → 捕获后记录 Error 日志，返回错误字符串（不抛出）
- SkillProvider.ExecuteAsync：按 ToolRegistry → SkillService → MCP 优先级路由，异常由对应层处理
- SkillService.RegisterSkill\<T\>：创建实例失败 → 捕获异常，记录 Error 日志，不抛出
- SkillService.RegisterSkill：名称重复 → 记录 Warning 日志，跳过注册

### 排障指南

| 现象 | 可能原因 | 排查方向 |
|------|---------|---------|
| Skill 未出现在工具集合 | 配置过滤 | 检查 SkillsConfig.EnabledSkills 和 Instances |
| Skill 未出现在工具集合 | 权限过滤 | 检查 SkillDescriptor 的 ACL 列表和用户上下文 |
| Skill 未出现在工具集合 | 注册缺失 | 检查 DI 注册和 RegisterSkill 调用 |
| Skill 执行失败 | 参数错误 | 检查 ParametersJsonSchema 和传入参数 |
| Skill 执行失败 | 实现异常 | 查看 Error 级别日志 |
