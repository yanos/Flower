#!/bin/bash
# Builds the server image from this working tree, uncommitted changes included,
# so a server change can be run in Docker without cutting a tag.
#
#   scripts/build-server-image.sh              # build flower-server:local
#   scripts/build-server-image.sh --up         # ...and restart the flower deployment on it
#   scripts/build-server-image.sh --up ~/docker/flower
#                                              # ...the deployment in that directory
#   scripts/build-server-image.sh --no-web-ui  # skip the browser UI (minutes faster)
#
# The image is tagged flower-server:local, never ghcr.io/yanos/flower-server:latest:
# shadowing the published tag locally would survive until the next
# `docker compose pull` and quietly make "latest" mean whatever was last built
# here. docker-compose.yml takes the image from FLOWER_IMAGE instead, so --up is
#
#   FLOWER_IMAGE=flower-server:local docker compose up -d
#
# run where the deployment lives - which is rarely this clone's docker/: the
# deployment is set up once, somewhere with a .env naming the music folder (or
# a compose file of its own) and whichever overrides it needs, and a clone
# checked out to build from has neither. In order:
#
#   - a directory given after --up: compose is run from inside it, exactly as
#     `docker compose up -d` typed there would be, compose file and .env and
#     all. The one form that needs no container to exist.
#   - the existing flower container's own record of the directory and compose
#     files it was started with. Stopped counts; `docker compose down` removes
#     the container and that record with it, which is what the form above is for.
#   - this clone's docker/, with docker-compose.non-linux.yml added anywhere but
#     Linux, where host networking cannot work.
#
# Either way the configuration is checked before the build, so a missing .env
# costs seconds rather than a build.
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
up_dir=
web_ui=true

while [ $# -gt 0 ]; do
  case "$1" in
    --up)
      up=true
      # The directory is optional, so only a next argument that is not itself
      # a flag is taken as one.
      if [ $# -gt 1 ] && [ "${2#-}" = "$2" ]; then
        up_dir=$2
        shift
      fi
      ;;
    --no-web-ui) web_ui=false ;;
    -h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $1 (see --help)" >&2; exit 2 ;;
  esac
  shift
done

# Where --up restarts: the directory compose runs from, and any arguments that
# name its files. Empty arguments mean compose finds them itself, the way it
# does for someone typing `docker compose up -d` in that directory.
workdir=
compose=()

# ${compose[@]+...} rather than "${compose[@]}": macOS still ships bash 3.2,
# where an empty array under set -u is an unbound variable.
run_compose() {
  (cd "$workdir" && FLOWER_IMAGE="$image" docker compose ${compose[@]+"${compose[@]}"} "$@")
}

if [ "$up" = true ]; then
  if [ -n "$up_dir" ]; then
    if [ ! -d "$up_dir" ]; then
      echo "error: $up_dir is not a directory" >&2
      exit 2
    fi
    workdir=$(cd "$up_dir" && pwd)
    echo "restarting the deployment in $workdir"
  else
    # The project name is pinned in docker-compose.yml. .Label rather than
    # index .Labels: docker ps hands its template the labels as one
    # comma-joined string, which index refuses.
    deployment=$(docker ps -a --filter label=com.docker.compose.project=flower \
      --format '{{ .Label "com.docker.compose.project.working_dir" }}|{{ .Label "com.docker.compose.project.config_files" }}' \
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
      echo "no flower deployment found; starting one from $workdir (pass --up <dir> to name another)"
    fi

    for file in "${files[@]}"; do
      if [ ! -f "$file" ]; then
        echo "error: $file, which the deployment was started with, no longer exists" >&2
        exit 1
      fi
      compose+=(-f "$file")
    done
  fi

  if ! run_compose config --quiet; then
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
  run_compose up -d
  echo "running $image - back to the published image with:"
  printf '  cd %q && docker compose' "$workdir"
  if [ ${#compose[@]} -gt 0 ]; then
    printf ' %q' "${compose[@]}"
  fi
  printf ' up -d\n'
fi
