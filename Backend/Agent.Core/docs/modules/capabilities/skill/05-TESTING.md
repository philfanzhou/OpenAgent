# Testing: 技能发现与执行

## 测试策略

技能发现与执行的测试围绕收集、过滤、执行路由三个核心链路展开。

## 单元测试

### SkillService

| 测试场景 | 验证点 |
|----------|--------|
| RegisterSkill 注册新技能 | GetSkills 返回包含新注册的技能 |
| RegisterSkill 名称重复 | 跳过注册，记录 Warning 日志 |
| GetSkills 仅返回运行时注册 | 返回 `_registeredSkills`（不再拼接 DI 注入） |
| ExecuteSkillAsync 正常执行 | 返回技能执行结果（直接调用 skill.ExecuteAsync） |
| ExecuteSkillAsync 技能未找到 | 抛出 ArgumentException |
| ExecuteSkillAsync 执行异常 | 返回 "Error: ..." 字符串，不抛出 |

### SkillProvider 收集

| 测试场景 | 验证点 |
|----------|--------|
| ToolRegistry 描述符收集 | 所有 ToolDefinition 转为 SkillDescriptor（Source=Local） |
| 本地 Skill 描述符收集 | ISkill 转为 SkillDescriptor，跳过同名项 |
| MCP 描述符收集 | IsConnected 时收集 MCP 工具，跳过同名项 |
| 动态描述符收集 | 运行时注册的描述符被包含，跳过同名项 |
| MCP 未连接 | 跳过 MCP 描述符收集 |

### SkillProvider 过滤

| 测试场景 | 验证点 |
|----------|--------|
| SkillsConfig 为 null | 不过滤，返回全部 |
| Instances 非空 | 按 Enabled=true 的 Name/Id 匹配 |
| EnabledSkills 非空 | 按 Name 匹配 |
| 配置存在但列表为空 | 返回空列表 |
| ACL 全空 | 允许所有用户 |
| ACL 非空 + userContext 为 null | 拒绝访问 |
| ACL 匹配 | 任一维度匹配即允许 |

### SkillProvider 执行路由

| 测试场景 | 验证点 |
|----------|--------|
| ToolRegistry 有该工具 | 路由到 ToolRegistry.ExecuteToolAsync |
| ISkillService 有该技能 | 路由到 SkillService.ExecuteSkillAsync |
| 其他 | 路由到 McpClient.CallToolAsync |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整收集-过滤-执行链路 | 从注册到执行结果返回的完整流程 |
| 配置过滤与权限过滤组合 | 两个过滤维度正确叠加 |
| 动态注册后立即可用 | RegisterSkill 后 GetSkillDescriptorsAsync 包含新技能 |

## 验收口径

- [ ] 四个渠道的描述符均能正确收集
- [ ] 配置过滤和权限过滤行为符合预期
- [ ] 执行路由优先级正确：ToolRegistry > ISkillService > MCP
- [ ] 同名技能去重（忽略大小写）
- [ ] 注册失败不中断系统运行
