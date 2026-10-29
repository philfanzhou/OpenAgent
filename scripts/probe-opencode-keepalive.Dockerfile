# Build the two inputs with Engine.Host/Dockerfile's build and opencode targets.
ARG ENGINE_BUILD_IMAGE=openagent-n04-build:local
ARG OPENCODE_TEST_IMAGE=openagent-n04-opencode:local
FROM ${OPENCODE_TEST_IMAGE} AS opencode
FROM ${ENGINE_BUILD_IMAGE}
USER root
WORKDIR /src
RUN (command -v ps && command -v lsof) || (apt-get update && apt-get install -y --no-install-recommends procps lsof && rm -rf /var/lib/apt/lists/*)
COPY --from=opencode /opencode /usr/local/bin/opencode
COPY --from=opencode /usr/local/bin/node /usr/local/bin/node
COPY scripts/probe-opencode-keepalive.py scripts/probe-opencode-keepalive.py
RUN chown -R 1654:1654 /src /home/app && chmod 700 /home/app
ENV HOME=/home/app
USER 1654
RUN dotnet build Backend/tests/OpenAgent.Runtime.Tests --no-restore --nologo -v q
CMD ["python3", "scripts/probe-opencode-keepalive.py", "--executable", "/usr/local/bin/opencode", "--output", "/tmp/n04-probe", "--owners", "2"]
