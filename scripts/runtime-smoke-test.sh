#!/usr/bin/env bash
#
# Proves that a scaffolded Foundry application actually runs.
#
# Everything else in this repository verifies that code is *generated* or that it *compiles*.
# Neither says the application works: the scaffolded project has previously compiled cleanly and
# then failed to serve a single request -- a circular DI registration, an unresolvable
# ICurrentUserContext, a Microsoft.OpenApi version mismatch, and a POST that could never bind
# because the entity id was `required` on the wire. Each was invisible to a build.
#
# This walks the path a user actually takes and asserts on HTTP responses and durability:
#
#   foundry new -> dotnet build -> boot -> GET -> POST -> GET by id -> kill -> restart -> GET by id
#
# The final step is the one that matters most: reading the record back from a *different process*
# proves it reached MongoDB rather than living in memory.
#
# Requires: .NET 10 SDK, and MongoDB on localhost:27017 (docker compose up -d).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$REPO_ROOT/foundry-cli/src/Foundry.Cli/Foundry.Cli.csproj"
WORK_DIR="$(mktemp -d)"
APP_NAME="SmokeTestApp"
APP_DIR="$WORK_DIR/$APP_NAME"
PORT_A=5310
PORT_B=5311
APP_PID=""

log()  { printf '\n\033[36m==> %s\033[0m\n' "$1"; }
pass() { printf '\033[32m  ok: %s\033[0m\n' "$1"; }
fail() { printf '\033[31m  FAIL: %s\033[0m\n' "$1" >&2; exit 1; }

cleanup() {
  if [[ -n "$APP_PID" ]] && kill -0 "$APP_PID" 2>/dev/null; then
    kill "$APP_PID" 2>/dev/null || true
    wait "$APP_PID" 2>/dev/null || true
  fi
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

# Starts the app on $1 and waits for it to answer. Fails with the log if it never comes up:
# a silent timeout here would be indistinguishable from a crash, which is the whole point.
start_app() {
  local port="$1" logfile="$2"
  ( cd "$APP_DIR" && dotnet run --no-build --urls "http://localhost:$port" >"$logfile" 2>&1 ) &
  APP_PID=$!

  local waited=0
  until curl -fsS -o /dev/null "http://localhost:$port/api/customers" 2>/dev/null; do
    if ! kill -0 "$APP_PID" 2>/dev/null; then
      echo "--- application log ---" >&2
      cat "$logfile" >&2
      fail "application exited during startup"
    fi
    sleep 2
    waited=$((waited + 2))
    if [[ $waited -ge 120 ]]; then
      echo "--- application log ---" >&2
      tail -40 "$logfile" >&2
      fail "application did not answer on port $port within 120s"
    fi
  done
}

stop_app() {
  kill "$APP_PID" 2>/dev/null || true
  wait "$APP_PID" 2>/dev/null || true
  APP_PID=""
}

log "Checking MongoDB is reachable"
# The suites that need a database fail rather than skip, and so does this: a smoke test that
# quietly skips its own subject is worse than no smoke test.
if ! (exec 3<>/dev/tcp/localhost/27017) 2>/dev/null; then
  fail "MongoDB is not listening on localhost:27017. Run 'docker compose up -d' first."
fi
pass "MongoDB reachable"

log "Building the CLI"
dotnet build "$CLI_PROJECT" -c Release -v q --nologo || fail "the CLI does not build"
CLI_DLL="$REPO_ROOT/foundry-cli/src/Foundry.Cli/bin/Release/net10.0/foundry.dll"
[[ -f "$CLI_DLL" ]] || fail "CLI assembly not found at $CLI_DLL"
pass "CLI built"

log "Scaffolding a project with 'foundry new'"
mkdir -p "$WORK_DIR"
# Invoked as the built assembly rather than via `dotnet run --project`, which sets the working
# directory to the project being run -- 'foundry new' scaffolds into the current directory, so
# that would create the project inside the CLI's own source tree instead of here.
( cd "$WORK_DIR" && dotnet "$CLI_DLL" new "$APP_NAME" ) \
  || fail "'foundry new' exited non-zero"

[[ -f "$APP_DIR/api-manifest.json" ]] \
  || fail "no api-manifest.json was generated; the app would serve no entity routes"
pass "project scaffolded with an api-manifest.json"

log "Building the scaffolded project"
( cd "$APP_DIR" && dotnet build -v q --nologo ) || fail "the scaffolded project does not compile"
pass "scaffolded project compiles"

log "Booting the application"
start_app "$PORT_A" "$WORK_DIR/app-a.log"
pass "application started and answered on /api/customers"

log "Both entities are routed"
for route in customers orders; do
  code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT_A/api/$route")
  [[ "$code" == "200" ]] || fail "GET /api/$route returned $code, expected 200"
  pass "GET /api/$route -> 200"
done

log "Creating a record"
create_body="$WORK_DIR/create.json"
create_code=$(curl -s -o "$create_body" -w '%{http_code}' \
  -X POST "http://localhost:$PORT_A/api/customers" \
  -H 'Content-Type: application/json' \
  -d '{"fullName":"Smoke Test","email":"smoke@example.com"}')

[[ "$create_code" == "201" ]] || {
  echo "response: $(cat "$create_body")" >&2
  fail "POST /api/customers returned $create_code, expected 201"
}

ENTITY_ID=$(python3 -c '
import json, sys
with open(sys.argv[1]) as handle:
    document = json.load(handle)
print(next((document[key] for key in ("Id", "id") if key in document), ""))
' "$create_body")

[[ -n "$ENTITY_ID" ]] || fail "the created record has no id in the response body"
pass "POST /api/customers -> 201, id $ENTITY_ID"

log "Reading it back in the same process"
read_code=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PORT_A/api/customers/$ENTITY_ID")
[[ "$read_code" == "200" ]] || fail "GET /api/customers/$ENTITY_ID returned $read_code, expected 200"
pass "GET by id -> 200"

log "Restarting the process"
stop_app
start_app "$PORT_B" "$WORK_DIR/app-b.log"
pass "application restarted"

log "The record survived the restart"
restart_body="$WORK_DIR/after-restart.json"
restart_code=$(curl -s -o "$restart_body" -w '%{http_code}' \
  "http://localhost:$PORT_B/api/customers/$ENTITY_ID")

[[ "$restart_code" == "200" ]] \
  || fail "after restart, GET /api/customers/$ENTITY_ID returned $restart_code -- the record did not persist"

grep -q 'smoke@example.com' "$restart_body" \
  || fail "the record read back after restart does not contain the value that was written"
pass "the record was served from MongoDB by a different process"

printf '\n\033[32mAll runtime checks passed: a scaffolded app boots, serves, persists, and survives a restart.\033[0m\n'
