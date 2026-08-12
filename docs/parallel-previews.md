# 并行预览实例

在多个 worktree 并行跑 agent 时，每个 agent 可以给自己部署一个**独立预览实例**供 review：
只启动 engine + router + chat，**复用主 openagent 栈的 postgres/redis/minio**（数据共享，不重建）。

## 机制

- `docker/preview.compose.yml` — 预览 compose：仅 engine/router/chat，经 `host.docker.internal` 直连主栈共享基础设施。
- `scripts/preview.sh` — 生命周期管理（`up`/`down`/`status`/`cleanup`），flock 原子分配端口与 Redis DB 序号。
- `.claude/skills/preview-instance/SKILL.md` — 给 agent 的操作说明（部署→报地址→等确认→销毁）。
- 隔离方式：
  - 端口：每个预览独占 engine(5210+)/router(5010+)/chat(8090+) 宿主端口。
  - Redis：共享同一个 Redis 服务器，每个预览用独立 DB 序号（`/1`…`/15`），注册表/限流/缓存互不干扰。
  - 数据：postgres/minio 与主实例同库同桶（agent/会话/文件共享）。
  - trace：`service.version=preview-<slug>` 便于在 Tempo/Grafana 中区分。

## 使用（WSL）

```bash
# 部署预览（在当前 worktree 根目录）
wsl -e bash -lc 'cd /mnt/d/Code/Agent.Matrix && bash scripts/preview.sh up <slug>'

# 查看所有预览
wsl -e bash -lc 'cd /mnt/d/Code/Agent.Matrix && bash scripts/preview.sh status'

# 销毁预览（用户确认后）
wsl -e bash -lc 'cd /mnt/d/Code/Agent.Matrix && bash scripts/preview.sh down <slug>'

# 清理 agent 崩溃残留的孤儿分配
wsl -e bash -lc 'cd /mnt/d/Code/Agent.Matrix && bash scripts/preview.sh cleanup'
```

> `up` 依赖主 openagent 栈的 postgres/redis/minio 在运行（未运行会报错并提示先启动主栈）。

## 注意

- 每个预览都会重新构建 engine/router/chat 镜像（对应其 worktree 代码），dotnet 构建较慢属正常。
- 并行建议不超过 2~3 个（每个含 3 个容器）。
- 共享同一个 postgres 库：若某 worktree 改动 EF 模型/迁移，会影响共享 schema，需谨慎。
