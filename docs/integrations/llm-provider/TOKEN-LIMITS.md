# 模型 Token 限制

## 配置与优先级

模型保持租户级独立选择，不绑定 Agent。执行时按以下顺序解析：

| 来源 | 上下文窗口 | 最大输出 |
|---|---|---|
| 单次 `ChatRequest` / `AgentRequest` | `contextWindowTokens` | `maxOutputTokens` |
| `AgentConfig` 默认值 | `contextWindowTokens` | `maxOutputTokens` |
| 所选 `LlmProviderProfile` | 必填 `contextTokens` | 可选 `maxOutputTokens` |

请求优先于 Agent 默认值，Agent 优先于模型默认值。模型 Profile 的值同时是硬上限；请求可超过 Agent 默认值，但不能超过模型上限。上下文和输出必须为正整数，最终输出必须小于最终上下文。空值表示继承，不能用 `0` 表示无限制。模型未声明最大输出时，平台不推测其上限。

例如模型声明 `contextTokens=128000`、`maxOutputTokens=16000`，Agent 默认 `64000/4000`，请求覆盖 `96000/8000`，则有效值是 `96000/8000`，用于历史压缩的输入预算为 `88000`。

`supportsMaxOutputTokens` 默认 `true`。设为 `false` 时，模型最大输出仍预留输入预算，但普通、流式和摘要调用均不设置 `ChatOptions.MaxOutputTokens`；Agent 或请求显式设置最大输出会报错。参数最终如何映射到各协议由现有官方 adapter 决定；开关控制平台传入的选项，不保证供应商协议层删除必填参数或默认值。

## 校验和持久化

管理接口在保存前校验正整数和输出/上下文关系，非法输入返回 HTTP 400 `InvalidRequest`。Agent 不绑定模型，因此与模型硬上限、参数支持能力的交叉校验延迟到执行时，非法已保存配置返回 `ConfigurationError`。非法请求覆盖返回 `InvalidRequest`，普通/流式路径均在客户端创建、附件会话创建和会话写入之前拒绝。

执行配置保留模型的连接参数和 `Modality`；请求覆盖不修改已保存 Profile。运行时 `TokenCapabilities` 不参与 JSON 序列化。用户消息 metadata 保存模型上限、Agent 默认、请求覆盖和有效值，并记录最大输出参数是否实际应用。Assistant 用量继续仅来自 Provider `UsageDetails`，不把本地估算写成实际用量。

数据库字段、迁移和缓存升级见 [配置表](../../database/tables/Configurations.md)。

## 压缩与限制

自动摘要按有效输入预算计算比例触发阈值和摘要目标；已有部署级固定触发阈值继续生效。摘要之后追加 MAF `ContextWindowCompactionStrategy`，在每次模型调用及工具迭代前按有效上下文减去最大输出收缩历史。完整持久化历史不删除，摘要审计逻辑保持不变。

自动和手动摘要均遵守参数支持开关。摘要使用同一模型时，推理生成余量还受模型最大输出硬上限约束；请求级回复上限不代替摘要预算。若 `SummaryModel` 指定其他模型，平台没有该模型的独立能力 Profile，不套用聊天模型的输出硬上限，需单独验证其能力。

MAF 历史压缩使用框架的 token 估算，不是供应商 tokenizer 的精确校验，也不能保证超长单条输入、多模态内容和工具定义一定落在真实窗口内。各 Provider 的真实参数映射及极端输入行为需要对应模型的集成验证。

实现：`AgentRuntimeResolver`、`ModelTokenLimitResolver`、`AgentFactory`、`ConversationHistoryFactory`、`OutputTokenLimitedChatClient`。
