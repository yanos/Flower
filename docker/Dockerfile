# syntax=docker/dockerfile:1

# ── Build ───────────────────────────────────────────────────────────────────
# Runs on the build host's architecture: the publish is framework-dependent and
# arch-neutral, so a multi-arch build needs no QEMU.
#
# Declared so the legacy builder (`docker build` with no buildx plugin) can be
# handed it as a --build-arg: it never sets BUILDPLATFORM itself, and a FROM
# sees only args declared above it. BuildKit still fills it in automatically.
ARG BUILDPLATFORM
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Build the browser UI too. Costs a few minutes in this stage only; without it
# a browser gets a "not deployed" placeholder.
ARG INCLUDE_WEB_UI=true

# Version override for builds without git tags (shallow clone, tarball).
# Normally empty: MinVer reads the tags from .git.
ARG VERSION=

WORKDIR /src

# The wasm workload for the browser UI, plus python3, which Emscripten needs
# and the SDK image lacks.
RUN if [ "$INCLUDE_WEB_UI" = "true" ]; then \
      apt-get update \
   && apt-get install -y --no-install-recommends python3 \
   && rm -rf /var/lib/apt/lists/* \
   && dotnet workload install wasm-tools --skip-sign-check; \
    fi

# Restore from project files alone, so a source edit doesn't re-download packages.
COPY Directory.Build.props Directory.Build.targets ./
COPY Flower.Core/Flower.Core.csproj Flower.Core/
COPY Flower.Server/Flower.Server.csproj Flower.Server/
COPY Flower/Flower.csproj Flower/
COPY Flower.Web/Flower.Web.csproj Flower.Web/
RUN dotnet restore Flower.Server/Flower.Server.csproj \
 && if [ "$INCLUDE_WEB_UI" = "true" ]; then dotnet restore Flower.Web/Flower.Web.csproj; fi

COPY . .

# Flower.Server's build publishes the browser UI itself when the workload is there.
RUN set -e; \
    args=""; \
    if [ -n "$VERSION" ]; then args="$args -p:MinVerVersionOverride=$VERSION"; fi; \
    if [ "$INCLUDE_WEB_UI" != "true" ]; then args="$args -p:IncludeWebUi=false"; fi; \
    dotnet publish Flower.Server/Flower.Server.csproj -c Release -o /app --nologo $args

# ── Runtime ─────────────────────────────────────────────────────────────────
# Debian, not Alpine: the server needs ICU (see InvariantGlobalization in its csproj).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# /data: database, device key, logs, flower-server.json. /music: the library, read-only.
# Set here so flower-server.json cannot override them.
ENV Flower__DataDirectory=/data \
    Flower__LibraryPaths__0=/music

# The server picks its own ports; clearing this silences a startup warning.
ENV ASPNETCORE_HTTP_PORTS=

# 4533 HTTP, 4534 HTTPS (self-signed from the device key, pinned by clients).
EXPOSE 4533 4534

COPY --from=build /app /app
WORKDIR /app

# Chown before VOLUME: changes after VOLUME are discarded. A fresh named volume
# then starts out owned by the app user (1654), so no setup is needed.
RUN mkdir -p /data /music && chown $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

ENTRYPOINT ["dotnet", "/app/Flower.Server.dll"]
