# Conventions: 技能发现与执行

## 命名约定

- Skill 名称应体现业务动作，而非实现细节
- 与 MCP 工具和系统保留工具区分清楚
- 长期稳定，不随实现细节频繁变化
- 名称比较忽略大小写（OrdinalIgnoreCase）

## 架构约定

- 机制留在 Core（ISkillProvider、ISkillService、IToolRegistry）
- 业务能力留在外部业务工程
- 示例 Skill 只用于演示和测试，不应被误认为 Core 内置业务能力

## 注册约定

- 同名 Skill 只注册一次，后到的重复注册会被跳过
- 优先级：ToolRegistry > ISkillService > MCP
- 动态注册的 Skill 需要同时注册到 ISkillService 和描述符列表

## 配置约定

- SkillsConfig 为 null 表示不过滤（全部可用）
- SkillsConfig 存在但 EnabledSkills 和 Instances 均为空 → 不启用任何 Skill
- Instance 的 Name 和 Id 均可用于匹配

## 权限约定

- 四个 ACL 列表（AllowedUserIds、AllowedGroups、AllowedTenantIds、AllowedRoles）均为空时，允许所有用户
- ACL 列表非空时，userContext 为 null 的请求将被拒绝
- 任一 ACL 维度匹配即允许（OR 语义）

## 可观测性约定

- Skill 注册重复：Warning 级别
- Skill 注册失败：Error 级别
- Skill 执行开始：Information 级别
- Skill 执行失败：Error 级别
- MCP 工具收集失败：Warning 级别

## 运行时要求

- 可被统一收集为工具描述
- 可在 Tool Calling 时被稳定执行
- 执行结果可回填到对话历史
- 出错时具备清晰日志和错误语义
