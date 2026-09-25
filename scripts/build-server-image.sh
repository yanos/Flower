#!/bin/bash
# Builds the server image from this working tree, uncommitted changes included,
# so a server change can be run in Docker without cutting a tag.
#
#   scripts/build-server-image.sh              # build flower-server:local
#   scripts/build-server-image.sh --up         # ...and restart docker/'s deployment on it
#   scripts/build-server-image.sh --no-web-ui  # skip the browser UI (minutes faster)
#
# The image is tagged flower-server:local, never ghcr.io/yanos/flower-server:latest:
# shadowing the published tag locally would survive until the next
# `docker compose pull` and quietly make "latest" mean whatever was last built
# here. docker-compose.yml takes the image from FLOWER_IMAGE instead, so --up is
#
#   FLOWER_IMAGE=flower-server:local docker compose up -d
#
# with docker-compose.non-linux.yml added anywhere but Linux, where host
# networking cannot work. It runs against the same data volume as the published image - that is
# the point, a new server tried on the real pairings and library - and going
# back is a plain `docker compose up -d` from docker/.
#
# The version comes from MinVer reading .git, same as the release build, so the
# image reports the height past the last tag (0.3.1-alpha.0.1, say) rather
# than posing as a release.
set -euo pipefail
cd "$(dirname "$0")/.."

image=flower-server:local
up=false
web_ui=true

for arg in "$@"; do
  case "$arg" in
    --up) up=true ;;
    --no-web-ui) web_ui=false ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $arg (see --help)" >&2; exit 2 ;;
  esac
done

build_args=(--build-arg INCLUDE_WEB_UI="$web_ui")

# Without the buildx plugin `docker build` is the legacy builder, which leaves
# BUILDPLATFORM empty, and the Dockerfile's first FROM refuses an empty
# platform. MacPorts' docker, for one, ships without buildx.
if ! docker buildx version >/dev/null 2>&1; then
  build_args+=(--build-arg BUILDPLATFORM="linux/$(docker version -f '{{.Server.Arch}}')")
fi

docker build \
  -f docker/Dockerfile \
  "${build_args[@]}" \
  -t "$image" \
  .

# The same check the release job makes before it publishes: a wasm-tools
# install that quietly failed still builds an image, one that serves the
# "not deployed" placeholder to every browser.
if [ "$web_ui" = true ] && ! docker run --rm --entrypoint sh "$image" -c 'test -s /app/wwwroot/index.html'; then
  echo "error: /app/wwwroot/index.html is missing from $image - the browser UI build was skipped" >&2
  exit 1
fi

echo "built $image"

if [ "$up" = true ]; then
  compose=(-f docker-compose.yml)
  if [ "$(uname)" != Linux ]; then
    compose+=(-f docker-compose.non-linux.yml)
  fi
  cd docker
  FLOWER_IMAGE="$image" docker compose "${compose[@]}" up -d
  echo "running $image - back to the published image with: cd docker && docker compose ${compose[*]} up -d"
fi
