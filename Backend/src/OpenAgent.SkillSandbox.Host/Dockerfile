FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Backend/ Backend/
RUN dotnet restore Backend/OpenAgent.sln
RUN dotnet publish Backend/src/OpenAgent.SkillSandbox.Host/OpenAgent.SkillSandbox.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl python3 util-linux \
    && useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin sandbox \
    && mkdir -p /run/openagent-sandbox /tmp/openagent-sandbox \
    && chown -R sandbox:sandbox /run/openagent-sandbox /tmp/openagent-sandbox \
    && chmod 0700 /run/openagent-sandbox /tmp/openagent-sandbox \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish/ .

USER sandbox
ENTRYPOINT ["dotnet", "OpenAgent.SkillSandbox.Host.dll"]
