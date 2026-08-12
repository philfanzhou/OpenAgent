---
name: preview-instance
description: Deploy an isolated preview instance of the current worktree (engine + router + chat, sharing the main stack's postgres/redis/minio), report a preview URL to the user, wait for the user to confirm they are done reviewing, then destroy it. Use whenever the user needs to review the actual effect of changes in this worktree, when another agent or branch needs its own runnable instance, or when asked for a preview address. Do NOT use for one-off throwaway checks that don't need the full stack.
---

# 并行预览实例（preview-instance）

让当前 worktree 以**独立预览实例**运行起来（只起 engine + router + chat，共享主 openagent 栈的 postgres/redis/minio），把预览地址报告给用户；用户确认看完后再销毁。多个 agent 可并行各自开预览，端口与 Redis DB 由脚本原子分配，互不冲突。

## 先决条件

- Docker 在 WSL 中。所有 docker 命令必须经 `wsl -e bash -lc '...'` 执行。
- 共享基础设施（主 openagent 栈的 postgres/redis/minio）必须已在运行。若未运行，在**主 worktree** 先执行 `docker compose up -d`。预览实例**禁止**自己起 postgres/redis/minio。
- 工作目录必须是**当前 worktree 根目录**（保证 build context 与镜像对应本 worktree 代码）。

## 完整生命周期

### 1. 部署预览

在**当前 worktree 根目录**执行（slug = 分支/特性名的净化形式，小写字母数字与短横线，例如 `feat-svg`）：

```bash
wsl -e bash -lc 'cd <worktree绝对路径> && bash scripts/preview.sh up <slug>'
```

脚本会自动：读取共享基础设施实际宿主端口 → flock 原子分配 engine/router/chat 端口与 Redis DB 序号 → 构建并启动本 worktree 的 engine+router+chat → 等待就绪 → 打印三个地址。

### 2. 确认就绪

- 用 `wsl -e bash -lc 'cd <worktree> && bash scripts/preview.sh status'` 确认该 slug 处于 running。
- curl 验证：`http://localhost:<chat端口>/` 返回 200，`http://localhost:<engine端口>/health/ready` 返回 200。

### 3. 向用户报告预览地址

清晰列出（勿只给一个链接）：

```
预览就绪（slug: <slug>）：
- 前端预览: http://localhost:<chat端口>
- Router:   http://localhost:<router端口>
- Engine:   http://localhost:<engine端口>  (Ready: .../health/ready)
请打开前端预览 review 效果。确认看完后我将销毁该实例。
```

说明该实例与主实例共享数据（agent/会话/文件），且 trace 中 `service.version=preview-<slug>` 可区分。

### 4. 等待用户确认

**在用户明确说"看完/确认/可以销毁/down"之前，绝不销毁实例。** 若中途失败或用户要求先改代码再重看：先 `down`，改完再 `up`（重新部署）。

### 5. 用户确认后销毁

```bash
wsl -e bash -lc 'cd <worktree> && bash scripts/preview.sh down <slug>'
```

确认输出包含"已销毁，槽位已释放"。向用户回报：预览已销毁、容器/卷/镜像已清除、端口已释放。

## 规则（必须遵守）

- **只允许用 `scripts/preview.sh`**，禁止直接 `docker compose up`（会端口冲突、占用主实例资源）。
- **禁止动主 openagent 栈与共享基础设施**：不重建、不删除 postgres/redis/minio、不 `docker compose down` 主栈。
- **幂等**：若 `up` 时该 slug 已有分配，脚本会复用；不要重复起两个同名预览。
- **失败必清理**：任何一步失败，先执行 `down <slug>` 不留孤儿，再向用户如实报告错误。
- **slug 唯一**：每个预览用自己特性名，避免与其他 agent 冲突。
- 用户 review 期间不要擅自销毁；若长时间等待且不确定，向用户询问。
