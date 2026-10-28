# OpenCode 版本由 Backend/opencode.version 锁定（scripts/build-images.* 与 Release 工作流读取该文件传入）；
# latest 仅保留给手动 docker build 的临时构建，升级版本需先通过 --target runtime-tests 真实进程回归。
FROM node:22-bookworm-slim AS opencode
ARG OPENCODE_VERSION=latest
RUN npm install --prefix /opt/opencode --ignore-scripts --no-audit --no-fund opencode-ai@${OPENCODE_VERSION} \
    && node -e 'const fs=require("fs"); const pkg="opencode-linux-"+process.arch+(process.arch==="x64"?"-baseline":"")+"/bin/opencode"; fs.copyFileSync(require.resolve(pkg,{paths:["/opt/opencode"]}),"/opencode"); fs.chmodSync("/opencode",0o755)'

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV NUGET_PACKAGES=/opt/nuget/packages

COPY Backend/ Backend/
RUN dotnet restore Backend/OpenAgent.sln
RUN dotnet publish Backend/src/OpenAgent.Engine.Host/OpenAgent.Engine.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# 显式目标：真实 Linux OpenCode + 本地模型/工具链路，不使用外部模型账户。
FROM build AS runtime-tests
COPY --from=opencode /opencode /usr/local/bin/opencode
RUN chown -R ${APP_UID}:${APP_UID} /src /home/app
ENV HOME=/home/app
USER $APP_UID
RUN OPENAGENT_TEST_OPENCODE=/usr/local/bin/opencode dotnet test Backend/tests/OpenAgent.Runtime.Tests --no-restore --nologo
RUN OPENAGENT_TEST_OPENCODE=/usr/local/bin/opencode dotnet test Backend/tests/OpenAgent.Engine.Tests --no-restore --nologo --filter FullyQualifiedName~OpenCodeSseTests

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG OPENCODE_VERSION=latest
WORKDIR /app
# python3 与 node 供放行的 OpenCode 原生工具（bash 等）和脚本在容器内执行；
# node 二进制直接取自上方 node:22 构建阶段，OpenCode 本体自包含、不依赖它。
# npm/corepack 不随镜像分发，需要包管理的脚本应走隔离 Runner。
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish/ .
COPY --from=opencode /opencode /usr/local/bin/opencode
COPY --from=opencode /usr/local/bin/node /usr/local/bin/node

LABEL org.opencontainers.image.title="OpenAgent Engine" \
      org.opencontainers.image.source="https://github.com/philfanzhou/OpenAgent" \
      io.openagent.opencode-version="${OPENCODE_VERSION}"

ENV ASPNETCORE_URLS=http://+:5208
ENV HOME=/home/app \
    OpenCode__WorkspaceRoot=/var/lib/openagent/opencode
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys /var/lib/openagent/opencode \
    && chown -R ${APP_UID}:${APP_UID} /home/app /var/lib/openagent \
    && chmod 700 /home/app /var/lib/openagent/opencode
USER $APP_UID
EXPOSE 5208
ENTRYPOINT ["dotnet", "OpenAgent.Engine.Host.dll"]
