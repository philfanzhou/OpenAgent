# Pipeline 中间件链 (AgentPipeline)

## 核心用户故事

作为上层服务，我希望通过 Pipeline 执行 Agent 请求，以便在核心业务逻辑前后插入认证、审计、追踪等横切关注点。

## 功能名称和一句话概括

Pipeline 中间件链 — 按注册顺序执行中间件，最终调用 AgentService 完成推理。

## 补充约束

- 中间件按注册顺序执行（先注册先执行前置逻辑，后执行后置逻辑）
- 流式和非流式共享同一组中间件，各自实现 InvokeAsync 和 InvokeStreamAsync
- Pipeline 不负责业务逻辑，仅负责中间件编排

## 关键验收条件摘要

- [ ] 请求经过所有注册中间件后到达 Service
- [ ] 中间件异常向上传播，不吞没
- [ ] 流式请求正确传递 CancellationToken
- [ ] AgentException 被捕获并转换为 AgentResponse（Success=false）

## 明确列出"范围外"

- 不负责具体中间件实现逻辑
- 不负责中间件注册顺序（由 DI 容器决定）

## 文档索引

- [02-SPEC.md](./02-SPEC.md) — 详细需求规格
- [03-DESIGN.md](./03-DESIGN.md) — 设计说明
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试计划
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 约定与规范
