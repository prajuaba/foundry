#!/usr/bin/env bash
#
# Runs the solution's tests, after checking that the infrastructure they need is actually there.
#
# Without this check a stopped container produces around a hundred failures, each taking a minute to
# time out, spread across four suites — and they look exactly like a code regression. That has cost
# real diagnosis time more than once: the honest read of a red run is "something broke", and the
# cheapest way to rule out the boring cause is to rule it out first.
#
# The runtime smoke test has always done this. `dotnet test` did not, which is why the failure mode
# kept recurring on the path people actually use.
#
# Arguments are passed through to `dotnet test`, so this is a drop-in replacement:
#
#     bash scripts/run-tests.sh                          # everything
#     bash scripts/run-tests.sh --filter FullyQualifiedName~Ownership
#
# Set FOUNDRY_SKIP_INFRA_CHECK=1 to run anyway.

set -euo pipefail

cd "$(dirname "$0")/.."

RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'

# host:port:what needs it:why
REQUIRED=(
  "localhost:27017:MongoDB replica set:FoundryMongo.Tests, Foundry.Api.Tests, Foundry.IntegrationTests"
  "localhost:27018:MongoDB standalone:the archival fallback, which cannot run on a replica set"
  "localhost:9092:Kafka broker:Foundry.Kafka.IntegrationTests"
)

listening() {
  # No nc/lsof dependency: bash opens the socket itself, and /dev/tcp is not available everywhere,
  # so fall back to python which every developer machine running this repo already has.
  python3 - "$1" "$2" <<'PY' 2>/dev/null
import socket, sys
s = socket.socket()
s.settimeout(3)
try:
    s.connect((sys.argv[1], int(sys.argv[2])))
except Exception:
    sys.exit(1)
finally:
    s.close()
PY
}

if [ "${FOUNDRY_SKIP_INFRA_CHECK:-0}" != "1" ]; then
  echo "==> Checking the infrastructure the suites need"

  MISSING=0
  for entry in "${REQUIRED[@]}"; do
    IFS=':' read -r host port what why <<<"$entry"

    if listening "$host" "$port"; then
      printf '  %s✓%s %-22s %s:%s\n' "$GREEN" "$RESET" "$what" "$host" "$port"
    else
      printf '  %s✗%s %-22s nothing listening on %s:%s\n' "$RED" "$RESET" "$what" "$host" "$port"
      printf '      needed by: %s\n' "$why"
      MISSING=1
    fi
  done

  if [ "$MISSING" -eq 1 ]; then
    echo
    echo "${RED}Infrastructure is missing, so these suites would fail by timing out — around a" >&2
    echo "hundred failures that look like a code regression and are not.${RESET}" >&2
    echo >&2
    echo "  docker compose up -d" >&2
    echo >&2
    echo "If a container was running a moment ago, it has died; 'docker compose up -d' restarts it." >&2
    echo "To run anyway: FOUNDRY_SKIP_INFRA_CHECK=1 bash scripts/run-tests.sh" >&2
    exit 1
  fi

  echo
fi

echo "==> dotnet test"
exec dotnet test Foundry.slnx "$@"
