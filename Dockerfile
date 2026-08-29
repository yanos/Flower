# syntax=docker/dockerfile:1

# ── Build ───────────────────────────────────────────────────────────────────
#
# Pinned to $BUILDPLATFORM rather than the image's target platform on purpose.
# This is a framework-dependent publish: the output is IL plus SQLitePCLRaw's
# runtimes/ folder, which carries the native e_sqlite3 for every RID and picks
# one at run time. Nothing produced here is architecture-specific, so an amd64
# host can build the payload that goes into the arm64 image directly instead of
# running the whole SDK under QEMU - the difference between a multi-arch build
# that takes minutes and one that takes most of an hour.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Whether to build the Avalonia WebAssembly browser UI into the image. On by
# default: without it the server still serves its API and its OpenSubsonic
# surface, but a browser reaching the settings page gets WebUiHosting's
# "not deployed" placeholder instead. It costs a wasm-tools workload install
# and an Emscripten link step - several minutes and a few hundred MB, all of it
# confined to this stage and none of it reaching the runtime image.
ARG INCLUDE_WEB_UI=true

# Normally empty. .git is deliberately *not* excluded by .dockerignore (it is
# ~40 MB), so MinVer computes the version from the tags here exactly as it does
# on a local build and a bare `docker build .` produces a correctly stamped
# image with no flags at all. Set this when building from a shallow clone or an
# exported tarball, where MinVer would otherwise fall back to 0.1.0-alpha.0
# without saying so. It maps to MinVerVersionOverride rather than Version
# because MinVer's own target assigns Version and would overwrite a -p:Version.
ARG VERSION=

WORKDIR /src

RUN if [ "$INCLUDE_WEB_UI" = "true" ]; then dotnet workload install wasm-tools --skip-sign-check; fi

# Restore against the project files alone, before the sources land, so that
# editing a .cs file doesn't re-download the whole package graph. The four
# copied here are Flower.Server plus its transitive project references
# (Flower.Core) and, for the browser UI, Flower.Web -> Flower -> Flower.Core.
COPY Directory.Build.props Directory.Build.targets ./
COPY Flower.Core/Flower.Core.csproj Flower.Core/
COPY Flower.Server/Flower.Server.csproj Flower.Server/
COPY Flower/Flower.csproj Flower/
COPY Flower.Web/Flower.Web.csproj Flower.Web/
RUN dotnet restore Flower.Server/Flower.Server.csproj \
 && if [ "$INCLUDE_WEB_UI" = "true" ]; then dotnet restore Flower.Web/Flower.Web.csproj; fi

COPY . .

# No separate step for the browser UI: Flower.Server's own build targets
# publish Flower.Web and copy the bundle into the publish directory when the
# Emscripten toolchain is present, and skip it silently when it is not - see
# "The browser UI" in Flower.Server.csproj. Installing the workload above is
# the whole of the wiring.
RUN set -e; \
    args=""; \
    if [ -n "$VERSION" ]; then args="$args -p:MinVerVersionOverride=$VERSION"; fi; \
    if [ "$INCLUDE_WEB_UI" != "true" ]; then args="$args -p:IncludeWebUi=false"; fi; \
    dotnet publish Flower.Server/Flower.Server.csproj -c Release -o /app --nologo $args

# ── Runtime ─────────────────────────────────────────────────────────────────
#
# Debian-based, not Alpine. Flower.Server deliberately does not set
# InvariantGlobalization - its csproj records the real bug that caused
# (invariant mode turns string.Normalize into a silent no-op, which breaks the
# decomposed/precomposed path matching the iTunes play-count importer is built
# on) - so the image has to carry ICU. Debian ships it; a musl image would need
# icu-libs installed by hand, which is the trade this declines.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# /data is everything the server owns and must survive an image rebuild:
# flower.db, the device key, the trusted- and denied-peer lists, the logs and
# the operator-editable flower-server.json. /music is where the library is
# expected to be mounted, read-only. Both are set as environment variables
# rather than baked into appsettings.json because the environment outranks the
# flower-server.json on the data volume, and a setting the deployment owns
# should not be overridable by a file the operator edits (see Program.cs on why
# the two config layers sit in that order).
ENV Flower__DataDirectory=/data \
    Flower__LibraryPaths__0=/music

# 4533 plain, 4534 TLS - the latter served with a certificate minted from the
# server's own device key, so paired Flower clients validate it without a CA
# (see ServerTls). Both are informational under `network_mode: host`, which is
# how this is meant to run - see docs/SELF-HOSTING.md for why.
EXPOSE 4533 4534

COPY --from=build /app /app
WORKDIR /app

# $APP_UID is the non-root uid the .NET base images ship (1654). The bind mount
# behind /data has to be writable by it; docker-compose.yml and SELF-HOSTING.md
# both cover that. Declared as a volume so that a `docker run` without an
# explicit mount keeps its database instead of discarding it with the container.
RUN mkdir -p /data /music && chown $APP_UID:$APP_UID /data
VOLUME /data
USER $APP_UID

ENTRYPOINT ["dotnet", "/app/Flower.Server.dll"]
