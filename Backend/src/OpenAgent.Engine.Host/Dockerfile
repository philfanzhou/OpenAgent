# OpenCode 不固定版本，默认安装最新发布并仅带入原生可执行文件；需要复现时用 --build-arg OPENCODE_VERSION=<version> 锁定。
FROM node:22-bookworm-slim AS opencode
ARG OPENCODE_VERSION=latest
RUN npm install --prefix /opt/opencode --ignore-scripts --no-audit --no-fund opencode-ai@${OPENCODE_VERSION} \
    && node -e 'const fs=require("fs"); const pkg="opencode-linux-"+process.arch+(process.arch==="x64"?"-baseline":"")+"/bin/opencode"; fs.copyFileSync(require.resolve(pkg,{paths:["/opt/opencode"]}),"/opencode"); fs.chmodSync("/opencode",0o755)'

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Backend/ Backend/
RUN dotnet restore Backend/OpenAgent.sln
RUN dotnet publish Backend/src/OpenAgent.Engine.Host/OpenAgent.Engine.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# 显式目标：真实 Linux OpenCode + 本地模型/工具链路，不使用外部模型账户。
FROM build AS runtime-tests
COPY --from=opencode /opencode /usr/local/bin/opencode
RUN OPENAGENT_TEST_OPENCODE=/usr/local/bin/opencode dotnet test Backend/tests/OpenAgent.Runtime.Tests --no-restore --nologo
RUN OPENAGENT_TEST_OPENCODE=/usr/local/bin/opencode dotnet test Backend/tests/OpenAgent.Engine.Tests --no-restore --nologo --filter FullyQualifiedName~OpenCodeSseTests

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish/ .
COPY --from=opencode /opencode /usr/local/bin/opencode

ENV ASPNETCORE_URLS=http://+:5208
EXPOSE 5208
ENTRYPOINT ["dotnet", "OpenAgent.Engine.Host.dll"]
