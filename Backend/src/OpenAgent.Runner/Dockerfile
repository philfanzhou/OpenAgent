FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY Backend/ Backend/
RUN dotnet restore Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj
RUN dotnet publish Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends bubblewrap python3 python3-venv util-linux \
       libreoffice-impress libreoffice-calc fonts-noto-cjk curl \
    && bwrap --help | grep -q -- --disable-userns \
    && rm -rf /var/lib/apt/lists/*
COPY Backend/src/OpenAgent.Runner/sandbox/requirements.txt /opt/openagent-code/requirements.txt
RUN python3 -m venv /opt/openagent-code/venv \
    && /opt/openagent-code/venv/bin/pip install --disable-pip-version-check --no-cache-dir \
       -r /opt/openagent-code/requirements.txt
COPY --from=build /app/publish/ .
RUN groupadd --system --gid 65532 openagent \
    && useradd --system --uid 65532 --gid 65532 --no-create-home --shell /usr/sbin/nologin openagent \
    && mkdir -p /var/lib/openagent-runner/workspaces \
    && chown -R openagent:openagent /var/lib/openagent-runner \
    && chmod 0700 /var/lib/openagent-runner /var/lib/openagent-runner/workspaces
ENV ASPNETCORE_URLS=http://+:5209
EXPOSE 5209
USER 65532:65532
ENTRYPOINT ["dotnet", "OpenAgent.Runner.dll"]
