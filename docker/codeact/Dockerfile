FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Backend/ Backend/
RUN dotnet restore Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj
RUN dotnet publish Backend/src/OpenAgent.Runner/OpenAgent.Runner.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        docker.io \
        ca-certificates \
        curl \
        fonts-noto-cjk \
        libreoffice-calc \
        libreoffice-impress \
        python3 \
        python-is-python3 \
        python3-pip \
        python3-venv \
        util-linux \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --system --gid 65532 openagent-runner \
    && useradd --system --uid 65532 --gid 65532 \
        --home-dir /var/lib/openagent-runner --no-create-home \
        --shell /usr/sbin/nologin openagent-runner \
    && install -d -m 0777 /input /work /output /tmp /run \
    && install -d -o 65532 -g 65532 -m 0700 /var/lib/openagent-runner/workspaces \
    && python3 -m venv /opt/openagent-code/venv

COPY Backend/src/OpenAgent.Runner/sandbox/requirements.txt /tmp/requirements.txt
RUN /opt/openagent-code/venv/bin/pip install \
        --disable-pip-version-check --no-cache-dir \
        --requirement /tmp/requirements.txt \
    && rm -f /tmp/requirements.txt

COPY --from=build /app/publish/ .
RUN chown -R root:root /app /opt/openagent-code \
    && chmod -R go-w /app /opt/openagent-code

ENV ASPNETCORE_URLS=http://+:5088 \
    Runner__DockerPath=/usr/bin/docker \
    Runner__Runtime=runsc \
    Runner__SandboxImage=openagent-codeact:local \
    Runner__SandboxPythonPath=/opt/openagent-code/venv/bin/python \
    Runner__WorkspaceRoot=/var/lib/openagent-runner/workspaces

USER 65532:65532
EXPOSE 5088
ENTRYPOINT ["dotnet", "OpenAgent.Runner.dll"]
