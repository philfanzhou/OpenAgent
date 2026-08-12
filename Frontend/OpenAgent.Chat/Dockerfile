FROM node:22-alpine AS build
WORKDIR /app

COPY Frontend/OpenAgent.Chat/package.json Frontend/OpenAgent.Chat/pnpm-lock.yaml Frontend/OpenAgent.Chat/pnpm-workspace.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile

COPY Frontend/OpenAgent.Chat/ ./
ARG VITE_OPENAGENT_ROUTER_BASE_URL=http://localhost:5001
ARG VITE_OPENAGENT_ENGINE_BASE_URL=http://localhost:5208
ARG VITE_OPENAGENT_TENANT_ID=development
ENV VITE_OPENAGENT_ROUTER_BASE_URL=$VITE_OPENAGENT_ROUTER_BASE_URL
ENV VITE_OPENAGENT_ENGINE_BASE_URL=$VITE_OPENAGENT_ENGINE_BASE_URL
ENV VITE_OPENAGENT_TENANT_ID=$VITE_OPENAGENT_TENANT_ID
RUN pnpm build

FROM nginx:1.27-alpine AS runtime
COPY --from=build /app/dist/ /usr/share/nginx/html/
EXPOSE 80
