# OpenAgent.Chat

Vue 3 + TypeScript + Vite 单页工作台。支持连接 OpenAgent Router 或直连 Engine：Router 负责意图选路、Engine 服务发现和第三方 Agent 转发；直连模式用于单 Engine 开发联调。

## 本地开发

```bash
pnpm install
pnpm dev
```

登录页默认连接 Router `http://localhost:5001`。Development 可填写任意非空账号密码建立 Basic 联调身份；该方式不校验真实密码，不能用于生产。Production 使用企业 IdP 的 OIDC Authorization Code + PKCE，API 仅接受经过 issuer、audience、签名和有效期校验的 JWT Bearer token。

连接地址、模式与租户配置可保存在 `localStorage`；凭据、OIDC 临时参数、access token 和 refresh token 只进入当前标签页的 `sessionStorage`。OIDC 会在 access token 到期前自动续期，token 绑定到登录时的服务地址，切换地址必须重新登录；退出会清除 token、OIDC state/PKCE verifier、会话数据与未发送内容。

## 验证

```bash
pnpm test
pnpm build
```

工作台内的“健康检查”页会从浏览器验证当前 Router 或 Engine 的 Live、Ready、Agent Catalog、Identity 和 Conversations。架构、接口和安全边界见 [`docs/modules/chat-workspace/`](../../docs/modules/chat-workspace.md)。

## 会话审计

工作台设置里的**「会话审计」**页使用当前登录身份直接进入 OpenAgent.Audit 审计台，无需再次输入账号密码。

| 项 | 说明 |
|---|---|
| 入口 | 工作台设置 → 会话审计 → 打开审计台 |
| 身份来源 | 当前标签页 `sessionStorage` 中的 OpenAgent access token |
| 传递方式 | 在新标签页打开 `{审计台}/ui/login?token={token}` |
| 审计台行为 | 用该 token 向 OpenAgent `/me` 换取身份，立即转为会话 Cookie，令牌不留在页面 |
| 权限 | 只进普通视图（脱敏内容）；查看原文需在审计台内单独登录管理员 |

**配置审计台地址**：默认取当前页面主机的 `:8100` 端口，可在「会话审计」页直接修改（保存在 `localStorage` 的 `openagent.audit.base-url`），或用构建变量预置：

```bash
VITE_OPENAGENT_AUDIT_BASE_URL=http://10.51.4.45:8100
```

未登录时该页会提示先完成登录，不会跳转。审计台不可达时浏览器会显示连接错误，工作台本身不受影响。

## 生产边界

生产环境必须通过 Router 接入可验证的企业身份提供方。登录页不解释角色、Agent ACL 或租户权限；401 会清理会话并跳转登录，403 保留身份并展示禁止访问状态。直连 Engine 会绕过 Router 的意图识别、服务发现、外部 Provider 和 Router 权限边界，只适合受控网络或开发联调。
