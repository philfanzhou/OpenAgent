FROM node:22-alpine AS build
WORKDIR /app

COPY Frontend/OpenAgent.Chat/package.json Frontend/OpenAgent.Chat/pnpm-lock.yaml Frontend/OpenAgent.Chat/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY Frontend/OpenAgent.Chat/ ./
RUN pnpm build

FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/dist/ /usr/share/nginx/html/
COPY Frontend/OpenAgent.Chat/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
