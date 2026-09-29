#!/bin/bash
# Builds the server image from this working tree, uncommitted changes included,
# so a server change can be run in Docker without cutting a tag.
#
#   scripts/build-server-image.sh              # build flower-server:local
#   scripts/build-server-image.sh --up         # ...and restart the flower deployment on it
#   scripts/build-server-image.sh --no-web-ui  # skip the browser UI (minutes faster)
#
# The image is tagged flower-server:local, never ghcr.io/yanos/flower-server:latest:
# shadowing the published tag locally would survive until the next
# `docker compose pull` and quietly make "latest" mean whatever was last built
# here. docker-compose.yml takes the image from FLOWER_IMAGE instead, so --up is
#
#   FLOWER_IMAGE=flower-server:local docker compose up -d
#
# run where the existing deployment was started, with the compose files it was
# started with. Docker records both on the container, and they are rarely this
# clone's docker/: the deployment is set up once, somewhere with a .env naming
# the music folder and whichever overrides (caddy, cloudflared) it needs, and a
# clone checked out to build from has neither. With no deployment to find, it is
# this clone's docker/, with docker-compose.non-linux.yml added anywhere but
# Linux, where host networking cannot work. Either way the configuration is
# checked before the build, so a missing .env costs seconds rather than a build.
#
# It runs against the same data volume as the published image - that is the
# point, a new server tried on the real pairings and library - and going back
# is the same command without FLOWER_IMAGE, which it prints at the end.
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

# Where --up restarts, as compose arguments: --project-directory so the .env
# beside the deployment is the one read, and every -f it was started with.
if [ "$up" = true ]; then
  # Stopped counts: a deployment taken down to make way for this is still the
  # one meant. The project name is pinned in docker-compose.yml.
  deployment=$(docker ps -a --filter label=com.docker.compose.project=flower \
    --format '{{ index .Labels "com.docker.compose.project.working_dir" }}|{{ index .Labels "com.docker.compose.project.config_files" }}' \
    | head -n 1)

  if [ -n "$deployment" ]; then
    workdir=${deployment%%|*}
    IFS=, read -r -a files <<< "${deployment#*|}"
    echo "restarting the deployment started from $workdir"
  else
    workdir=$PWD/docker
    files=("$workdir/docker-compose.yml")
    if [ "$(uname)" != Linux ]; then
      files+=("$workdir/docker-compose.non-linux.yml")
    fi
    echo "no flower deployment found; starting one from $workdir"
  fi

  compose=(--project-directory "$workdir")
  for file in "${files[@]}"; do
    if [ ! -f "$file" ]; then
      echo "error: $file, which the deployment was started with, no longer exists" >&2
      exit 1
    fi
    compose+=(-f "$file")
  done

  if ! FLOWER_IMAGE="$image" docker compose "${compose[@]}" config --quiet; then
    echo "error: the compose configuration in $workdir does not resolve (is there a .env there?) - nothing built" >&2
    exit 1
  fi
fi

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
  FLOWER_IMAGE="$image" docker compose "${compose[@]}" up -d
  echo "running $image - back to the published image with:"
  printf '  docker compose'
  printf ' %q' "${compose[@]}"
  printf ' up -d\n'
fi
