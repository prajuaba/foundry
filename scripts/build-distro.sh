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

echo "==> Publishing foundry ($RID)"
rm -rf "$OUT"
dotnet publish "$ROOT/foundry-cli/src/Foundry.Cli/Foundry.Cli.csproj" \
  --configuration Release \
  --runtime "$RID" \
  -p:SelfContained=true \
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

"$BIN" schema build -i "$SCHEMA" -o "$WORK/Generated" -m "$WORK/Generated/api-manifest.json" >/dev/null
[ -f "$WORK/Generated/api-manifest.json" ] || { echo "  FAIL: no manifest emitted" >&2; exit 1; }
echo "  ok: schema build emitted $(find "$WORK/Generated" -name '*.cs' | wc -l | tr -d ' ') source files and a manifest"

# The embedded Studio asset. Checked by serving it, because its absence is only a build warning.
PORT=5099
"$BIN" studio --port "$PORT" >"$WORK/studio.log" 2>&1 &
STUDIO_PID=$!
trap 'kill "$STUDIO_PID" 2>/dev/null || true; rm -rf "$WORK"' EXIT

for _ in $(seq 1 30); do
  if curl -fsS "http://localhost:$PORT/" -o "$WORK/page.html" 2>/dev/null; then break; fi
  sleep 1
done

if grep -q 'id="root"' "$WORK/page.html" 2>/dev/null; then
  echo "  ok: embedded Studio bundle served ($(wc -c <"$WORK/page.html" | tr -d ' ') bytes)"
else
  echo "  FAIL: 'foundry studio' did not serve the embedded bundle." >&2
  tail -20 "$WORK/studio.log" >&2 || true
  exit 1
fi

kill "$STUDIO_PID" 2>/dev/null || true
wait "$STUDIO_PID" 2>/dev/null || true

echo
echo "Built and verified: $BIN ($(du -h "$BIN" | cut -f1), $RID)"
