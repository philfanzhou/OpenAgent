FROM node:22-alpine AS build
WORKDIR /app

COPY Frontend/OpenAgent.Chat/package.json Frontend/OpenAgent.Chat/pnpm-lock.yaml Frontend/OpenAgent.Chat/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY Frontend/OpenAgent.Chat/ ./
RUN pnpm build

FROM nginx:1.27-alpine AS runtime
# 公开地址不进入构建：入口脚本在容器启动时按 OPENAGENT_* 环境变量
# （与 deploy/ 环境文件同名）生成 /usr/share/nginx/html/runtime-config.js，
# 浏览器经 window.__OPENAGENT_RUNTIME_CONFIG__ 读取，同一镜像可服务任意域名。
COPY --from=build /app/dist/ /usr/share/nginx/html/
COPY Frontend/OpenAgent.Chat/docker/default.conf /etc/nginx/conf.d/default.conf
COPY Frontend/OpenAgent.Chat/docker/40-openagent-runtime-config.sh /docker-entrypoint.d/40-openagent-runtime-config.sh
RUN chmod +x /docker-entrypoint.d/40-openagent-runtime-config.sh
EXPOSE 80
