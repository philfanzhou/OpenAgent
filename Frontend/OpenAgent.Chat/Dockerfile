FROM node:22-alpine AS build
WORKDIR /app

COPY Frontend/OpenAgent.Chat/package.json Frontend/OpenAgent.Chat/pnpm-lock.yaml Frontend/OpenAgent.Chat/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY Frontend/OpenAgent.Chat/ ./
RUN pnpm build

FROM nginx:1.27-alpine AS runtime
RUN apk add --no-cache jq
COPY --from=build /app/dist/ /usr/share/nginx/html/
COPY Frontend/OpenAgent.Chat/nginx.conf /etc/nginx/conf.d/default.conf
COPY --chmod=755 Frontend/OpenAgent.Chat/docker-entrypoint.d/40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh
EXPOSE 80
