#!/usr/bin/env bash
#
# Builds the distributable `foundry` binary into dist-bin/.
#
# Two things about this build are not discoverable from the csproj, and getting either wrong
# produces a result that looks fine:
#
#   1. `-p:SelfContained=true` has to be passed on the command line. The CLI project sets
#      PublishSelfContained, but that does not flow to referenced projects, and the schema compiler
#      is an executable project. Without the flag the publish fails with NETSDK1150; passing it as a
#      *global* property is what makes the whole graph agree.
#
#   2. The Studio web bundle has to exist before the publish. It is embedded as a resource, and the
#      csproj only *warns* when it is missing — so a publish without it succeeds and silently ships a
#      binary whose `foundry studio` command does not work.
#
# So this script builds the bundle first, publishes, and then runs the binary to check it. A build
# that cannot answer `foundry version` is not a build worth keeping.

set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"

RID="${1:-}"
VERSION="${2:-}"
if [ -z "$RID" ]; then
  case "$(uname -s)/$(uname -m)" in
    Darwin/arm64)  RID=osx-arm64  ;;
    Darwin/x86_64) RID=osx-x64    ;;
    Linux/aarch64) RID=linux-arm64;;
    Linux/x86_64)  RID=linux-x64  ;;
    *) echo "Cannot infer a runtime identifier for $(uname -s)/$(uname -m)." >&2
       echo "Pass one explicitly, e.g. $0 linux-x64" >&2
       exit 1 ;;
  esac
fi

# Default version to the exact tag on HEAD (stripped of its 'v' prefix), or 1.0.0 with no tag.
# --always would make git describe never fail, silently returning a bare commit hash instead --
# and a bare hash is not a valid MSBuild <Version>, which is exactly what broke CI.
if [ -z "$VERSION" ]; then
  VERSION=$((git describe --tags --exact-match 2>/dev/null || echo "1.0.0") | sed 's/^v//')
fi

OUT="$ROOT/dist-bin"

echo "==> Studio web bundle"
if [ ! -d "$ROOT/foundry-studio/node_modules" ]; then
  ( cd "$ROOT/foundry-studio" && npm ci )
fi
( cd "$ROOT/foundry-studio" && npm run build )

BUNDLE="$ROOT/foundry-studio/dist/index.html"
if [ ! -f "$BUNDLE" ]; then
  echo "Studio bundle was not produced at $BUNDLE; refusing to ship a binary without it." >&2
  exit 1
fi

echo "==> Publishing foundry ($RID) version $VERSION"
rm -rf "$OUT"
dotnet publish "$ROOT/foundry-cli/src/Foundry.Cli/Foundry.Cli.csproj" \
  --configuration Release \
  --runtime "$RID" \
  -p:SelfContained=true \
  -p:Version="$VERSION" \
  --output "$OUT"

BIN="$OUT/foundry"
[ -x "$BIN" ] || { echo "No executable at $BIN after publish." >&2; exit 1; }

echo "==> Verifying the binary"

"$BIN" version

# No arguments must fail. It returns 1 precisely so `foundry $UNSET_VAR` cannot look like success.
if "$BIN" >/dev/null 2>&1; then
  echo "  FAIL: 'foundry' with no arguments exited 0; it must exit non-zero." >&2
  exit 1
fi
echo "  ok: no-argument invocation exits non-zero"

# Compile the showcase schema, which exercises every construct the IR declares.
SCHEMA="$ROOT/samples/Foundry.E2E.Showcase/e2e-schema.ir.json"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

"$BIN" validate "$SCHEMA" >/dev/null
echo "  ok: validate"

# doctor exits non-zero when a required prerequisite is missing. A machine that just published this
# binary has the SDK, so 0 is the expected answer -- and a non-zero here means either the machine is
# genuinely short of something or the command is misreporting, both worth stopping for.
"$BIN" doctor >/dev/null
echo "  ok: doctor"

"$BIN" schema build -i "$SCHEMA" -o "$WORK/Generated" -m "$WORK/Generated/api-manifest.json" >/dev/null
[ -f "$WORK/Generated/api-manifest.json" ] || { echo "  FAIL: no manifest emitted" >&2; exit 1; }
echo "  ok: schema build emitted $(find "$WORK/Generated" -name '*.cs' | wc -l | tr -d ' ') source files and a manifest"

# The embedded Studio asset. Checked by serving it, because its absence is only a build warning.
#
# The loop waits for the page to be *right*, not merely for curl to connect. Waiting on the
# connection alone made this flaky: the listener accepts before the app has finished starting, so an
# early request could return a body without the SPA marker and the check would fail a working build.
# A gate that fails at random teaches people to re-run it, which is how a real failure gets waved
# through.
PORT="${FOUNDRY_STUDIO_PORT:-$((20000 + RANDOM % 20000))}"
"$BIN" studio --port "$PORT" >"$WORK/studio.log" 2>&1 &
STUDIO_PID=$!
trap 'kill "$STUDIO_PID" 2>/dev/null || true; rm -rf "$WORK"' EXIT

SERVED=0
for _ in $(seq 1 45); do
  # If the server died there is nothing to wait for; report immediately rather than after 45s.
  if ! kill -0 "$STUDIO_PID" 2>/dev/null; then
    echo "  FAIL: 'foundry studio' exited before serving anything." >&2
    tail -30 "$WORK/studio.log" >&2 || true
    exit 1
  fi

  if curl -fsS --max-time 5 "http://localhost:$PORT/" -o "$WORK/page.html" 2>/dev/null \
     && grep -q 'id="root"' "$WORK/page.html"; then
    SERVED=1
    break
  fi

  sleep 1
done

if [ "$SERVED" -eq 1 ]; then
  echo "  ok: embedded Studio bundle served ($(wc -c <"$WORK/page.html" | tr -d ' ') bytes)"
else
  echo "  FAIL: 'foundry studio' did not serve the embedded bundle on port $PORT." >&2
  tail -30 "$WORK/studio.log" >&2 || true
  exit 1
fi

kill "$STUDIO_PID" 2>/dev/null || true
wait "$STUDIO_PID" 2>/dev/null || true

echo
echo "Built and verified: $BIN ($(du -h "$BIN" | cut -f1), $RID)"
