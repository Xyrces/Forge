# syntax=docker/dockerfile:1.7
# Build with --secret id=nuget_config,src=<private authenticated config>.
# Credentials never enter ARG/ENV, image history, or runtime layers.
ARG SDK_IMAGE
FROM ${SDK_IMAGE} AS command-helper
RUN apt-get update && apt-get install -y --no-install-recommends golang-go \
    && rm -rf /var/lib/apt/lists/*
COPY deploy/k3s/command-server/*.go /src/
RUN go test /src/main.go /src/main_test.go
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /forge-command-server /src/main.go

FROM ${SDK_IMAGE} AS build
RUN apt-get update && apt-get install -y --no-install-recommends git python3 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /source
COPY . .
RUN --mount=type=secret,id=nuget_config,required=true \
    dotnet restore Forge.sln --configfile /run/secrets/nuget_config
RUN dotnet build Forge.sln -c Release --no-restore
RUN dotnet test tests/Forge.Tests/Forge.Tests.csproj -c Release --no-build \
    --filter 'FullyQualifiedName!~RealLlmIntegrationTests&FullyQualifiedName!~MafAgentRunnerLiveTests&FullyQualifiedName!~MeshyLiveSmokeTests'
RUN dotnet publish Forge.Core/Forge.Core.csproj -c Release --no-restore -o /publish
RUN test -f /publish/Forge.Core.dll && test -f /publish/wwwroot/app.css && test -f /publish/agents/coredev.md

# SDK is deliberate: the same pinned artifact supports Forge CPU implementation/testing.
FROM ${SDK_IMAGE} AS runtime
USER root
RUN apt-get update && apt-get install -y --no-install-recommends git python3 ca-certificates curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 forge && useradd --uid 10001 --gid 10001 --create-home forge \
    && install -d -o forge -g forge /var/lib/forge \
    && git config --system user.name 'Forge' && git config --system user.email 'forge@local' \
    && git config --system core.hooksPath /dev/null
WORKDIR /app
COPY --from=build /publish/ ./
COPY --from=command-helper /forge-command-server /app/forge-command-server
COPY deploy/k3s/ /app/deploy/k3s/
ARG SOURCE_REVISION
RUN printf '%s' "$SOURCE_REVISION" > /app/source-revision
LABEL org.opencontainers.image.revision=${SOURCE_REVISION}
# The PVC mount root always exists. A nested home directory would be hidden by
# a fresh volume and breaks SDK first-use before an agent can run verification.
ENV HOME=/var/lib/forge DOTNET_CLI_HOME=/var/lib/forge DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV GIT_AUTHOR_NAME=Forge GIT_AUTHOR_EMAIL=forge@localhost GIT_COMMITTER_NAME=Forge GIT_COMMITTER_EMAIL=forge@localhost
USER 10001:10001
ENTRYPOINT ["dotnet", "/app/Forge.Core.dll"]
