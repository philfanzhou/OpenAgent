FROM node:22-alpine AS build
WORKDIR /app

COPY Frontend/OpenAgent.Chat/package.json Frontend/OpenAgent.Chat/pnpm-lock.yaml Frontend/OpenAgent.Chat/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY Frontend/OpenAgent.Chat/ ./
RUN pnpm build

FROM nginx:1.27-alpine AS runtime
COPY Frontend/OpenAgent.Chat/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/ /usr/share/nginx/html/
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
  # alpine 里 localhost 先解析到 IPv6 的 ::1，而 nginx 只监听 IPv4；用 127.0.0.1 才能连上。
  CMD wget -q -O /dev/null http://127.0.0.1/ || exit 1
