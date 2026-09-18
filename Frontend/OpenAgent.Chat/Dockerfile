FROM node:22-alpine AS build
WORKDIR /app

COPY Frontend/OpenAgent.Chat/package.json Frontend/OpenAgent.Chat/pnpm-lock.yaml Frontend/OpenAgent.Chat/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY Frontend/OpenAgent.Chat/ ./
# 构建参数与 deploy/ 环境文件同名的统一变量；默认值对齐 Nginx 公开端口，
# 直接 docker build --build-arg OPENAGENT_PUBLIC_HOST=... 即可，无需经过 scripts/build-images.*。
# 镜像内拼出浏览器端地址并注入 VITE_*：Vite 仅内联 VITE_ 前缀变量，浏览器端也无法读取运行时环境。
ARG OPENAGENT_PUBLIC_SCHEME=https
ARG OPENAGENT_PUBLIC_HOST=localhost
ARG OPENAGENT_ROUTER_PORT=8082
ARG OPENAGENT_ENGINE_PORT=8083
ARG OPENAGENT_TENANT_ID=development
ENV VITE_OPENAGENT_ROUTER_BASE_URL=${OPENAGENT_PUBLIC_SCHEME}://${OPENAGENT_PUBLIC_HOST}:${OPENAGENT_ROUTER_PORT}
ENV VITE_OPENAGENT_ENGINE_BASE_URL=${OPENAGENT_PUBLIC_SCHEME}://${OPENAGENT_PUBLIC_HOST}:${OPENAGENT_ENGINE_PORT}
ENV VITE_OPENAGENT_TENANT_ID=${OPENAGENT_TENANT_ID}
RUN pnpm build

FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/dist/ /usr/share/nginx/html/
EXPOSE 80
