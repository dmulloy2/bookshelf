#!/usr/bin/env bash
# Build the bookshelf image on a remote Docker host from the working tree, without waiting for CI, and smoke-test it.
#
#   docker/dev/vm-build.sh                 sync, build backend + UI + image, run the smoke test
#   docker/dev/vm-build.sh --no-smoke      stop after the image is built (tagged bookshelf:dev on the host)
#   docker/dev/vm-build.sh --add "title"   also add a book the way the UI does (needs a metadata service)
#   docker/dev/vm-build.sh --keep          leave the smoke-test container running to poke at
#   docker/dev/vm-build.sh --clean         remove the remote build tree, caches and image
#
# Environment:
#   BUILD_HOST      ssh host with Docker on linux/amd64 (default: docker)
#   METADATA_URL    metadata service for the smoke container (default: http://rreading-glasses:8788)
#   SDK_IMAGE       .NET SDK image to build with (default: mcr.microsoft.com/dotnet/sdk:10.0)
#   BUILD_CPUS      CPUs the build may use (default: 3, so the host keeps one for its other services)
#
# The image is for testing. CI stays the source of truth for what gets deployed: it builds the registry image that the
# digest-pinning policy expects, and runs the full test suite.
set -euo pipefail

HOST=${BUILD_HOST:-docker}
METADATA_URL=${METADATA_URL:-http://rreading-glasses:8788}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
NODE_IMAGE=${NODE_IMAGE:-node:20-bookworm}
CPUS=${BUILD_CPUS:-3}
TAG=bookshelf:dev
RID=linux-musl-x64
FRAMEWORK=net10.0
VERSION=0.4.21.0

SMOKE=yes
KEEP=no
CLEAN=no
ADD_TERM=

while [ $# -gt 0 ]; do
  case "$1" in
    --no-smoke) SMOKE=no ;;
    --keep) KEEP=yes ;;
    --clean) CLEAN=yes ;;
    --add) ADD_TERM="${2:?--add needs a search term}"; shift ;;
    -h|--help) sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

cd "$(git rev-parse --show-toplevel)"
HERE=docker/dev
step() { printf '\n==> %s\n' "$*"; }
remote() { ssh "$HOST" "$@"; }

if [ "$CLEAN" = yes ]; then
  step "Cleaning $HOST"
  remote 'docker rm -f bookshelf-dev >/dev/null 2>&1 || true
          docker run --rm -v "$HOME/bookshelf-dev:/w" alpine sh -c "rm -rf /w/*" 2>/dev/null || true
          rm -rf "$HOME/bookshelf-dev"
          docker volume rm bsdev-nuget bsdev-yarn >/dev/null 2>&1 || true
          docker image rm bookshelf:dev >/dev/null 2>&1 || true
          echo done'
  exit 0
fi

# Everything below runs under one lock on the host, so two builds never fight over the same tree or CPUs.
step "Syncing the working tree to $HOST"
# A snapshot of what git would commit: tracked and untracked files minus ignored ones, with LF line endings. Git's
# checkout on some machines is CRLF, and CRLF in the image's s6 init scripts stops the container from starting.
INDEX=$(mktemp -u)
trap 'rm -f "$INDEX"' EXIT
GIT_INDEX_FILE=$INDEX git read-tree HEAD
GIT_INDEX_FILE=$INDEX git -c core.safecrlf=false add -A 2>/dev/null
TREE=$(GIT_INDEX_FILE=$INDEX git write-tree)
DIRTY=$(git status --porcelain | grep -q . && echo -dirty || true)
COMMIT=$(git rev-parse --short HEAD)${DIRTY}

# autocrlf would turn the stored LF back into CRLF on the way out of git archive, so switch it off for the export.
git -c core.autocrlf=false archive --format=tar "$TREE" | remote 'set -e
  mkdir -p "$HOME/bookshelf-dev/tree"
  cd "$HOME/bookshelf-dev"
  rm -rf snapshot && mkdir snapshot
  tar -x -C snapshot
  # Checksum sync keeps the mtime of unchanged files, so MSBuild only rebuilds what changed.
  # Build output and caches are git-ignored (and root-owned, as the containers run as root), so leave them alone.
  rsync -rc --delete --filter=":- .gitignore" snapshot/ tree/
  rm -rf snapshot'

FRONT_STAMP=$(git ls-files -z frontend yarn.lock package.json | xargs -0 shasum | shasum | cut -d' ' -f1)

step "Building on $HOST ($COMMIT)"
remote "bash -s" -- "$SDK_IMAGE" "$NODE_IMAGE" "$CPUS" "$RID" "$FRAMEWORK" "$VERSION" "$TAG" "$COMMIT" "$FRONT_STAMP" <<'REMOTE'
set -euo pipefail
SDK_IMAGE=$1; NODE_IMAGE=$2; CPUS=$3; RID=$4; FRAMEWORK=$5; VERSION=$6; TAG=$7; COMMIT=$8; FRONT_STAMP=$9
cd "$HOME/bookshelf-dev"
exec 9>.lock
flock -n 9 || { echo "another build is running on this host"; exit 1; }
T0=$(date +%s)
lap() { printf '    (%ss)\n' "$(( $(date +%s) - T0 ))"; T0=$(date +%s); }

echo "-- backend ($RID)"
docker run --rm --cpus="$CPUS" -v "$PWD/tree:/src" -v bsdev-nuget:/root/.nuget/packages -w /src \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 "$SDK_IMAGE" \
  dotnet msbuild -restore src/Readarr.sln -p:SelfContained=true -p:Configuration=Release -p:Platform=Posix \
    -p:RuntimeIdentifiers="$RID" -p:AssemblyVersion="$VERSION" -t:PublishAllRids -v:q -nologo
lap

if [ "$(cat .front-stamp 2>/dev/null || true)" = "$FRONT_STAMP" ] && [ -d tree/_output/UI ]; then
  echo "-- UI unchanged, skipping"
else
  echo "-- UI"
  docker run --rm --cpus="$CPUS" -v "$PWD/tree:/src" -v bsdev-yarn:/usr/local/share/.cache/yarn -w /src \
    -e NODE_OPTIONS=--max-old-space-size=4096 "$NODE_IMAGE" \
    sh -c 'yarn install --frozen-lockfile --network-timeout 120000 --silent && yarn run build --env production'
  echo "$FRONT_STAMP" > .front-stamp
  lap
fi

echo "-- image $TAG"
# CRLF in the s6 service files stops the container from starting ("must be oneshot, longrun, or bundle"), and nothing
# in the build notices. Fail here instead.
if grep -rIlq "$(printf '\r')" tree/docker/root; then
  echo "CRLF line endings in docker/root:"; grep -rIl "$(printf '\r')" tree/docker/root | head -5; exit 1
fi
test -d "tree/_output/$FRAMEWORK/$RID" || { echo "missing tree/_output/$FRAMEWORK/$RID"; exit 1; }
rm -rf image && mkdir -p "image/_output/$FRAMEWORK"
cp -a "tree/_output/$FRAMEWORK/$RID" "image/_output/$FRAMEWORK/$RID"
cp -a tree/_output/UI image/_output/UI
cp -a tree/docker image/docker
rm -rf image/docker/dev
docker build -q -f image/docker/Dockerfile -t "$TAG" \
  --build-arg HARDCOVER=true --build-arg METADATA_URL=https://hardcover.bookinfo.pro \
  --build-arg GIT_BRANCH=dev --build-arg COMMIT_HASH="$COMMIT" --build-arg BUILD_DATE="$(date +%F)" image >/dev/null
rm -rf image
lap
docker image inspect "$TAG" --format '   {{.Id}}  {{.Size}} bytes'
REMOTE

if [ "$SMOKE" = no ]; then
  step "Built $TAG on $HOST"
  exit 0
fi

step "Smoke test"
remote 'docker rm -f bookshelf-dev >/dev/null 2>&1 || true'
# Seed a config with forms authentication on, as a real install has it: a fresh one starts with none, and then the login
# redirect would not be exercised. The app fills in the rest on first start.
CONFIG=$(remote 'd=$(mktemp -d "$HOME/bookshelf-dev/config.XXXXXX"); chmod 777 "$d"
  key=$(head -c 16 /dev/urandom | od -An -tx1 | tr -d " \n")
  printf "<Config>\n  <ApiKey>%s</ApiKey>\n  <AuthenticationMethod>Forms</AuthenticationMethod>\n  <AuthenticationRequired>Enabled</AuthenticationRequired>\n</Config>\n" "$key" > "$d/config.xml"
  chmod 666 "$d/config.xml"; echo "$d"')
NET=$(remote 'docker network inspect docker_default >/dev/null 2>&1 && echo docker_default || echo bridge')
remote "docker run -d --name bookshelf-dev --network $NET --user 1000:1000 -e METADATA_URL='$METADATA_URL' -v '$CONFIG:/config' $TAG >/dev/null"

# printf %q escapes each argument for the remote shell, so a title like Can't Help Falling survives.
ARGS=$(printf ' %q' bookshelf-dev "$CONFIG" ${ADD_TERM:+--add "$ADD_TERM"})
set +e
remote "python3 -$ARGS" < "$HERE/smoke.py"
RC=$?
set -e

if [ "$KEEP" = yes ]; then
  echo "left running: ssh $HOST docker logs bookshelf-dev   (config in $CONFIG)"
else
  remote "docker rm -f bookshelf-dev >/dev/null 2>&1; rm -rf '$CONFIG'"
fi
exit $RC
