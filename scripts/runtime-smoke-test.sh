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
# Two applications are scaffolded, because they prove different things:
#
#   Phase 1 (default schema)     the CRUD contract -- create, read, update, delete, filter,
#                                validate, concurrency, and durability across a restart.
#   Phase 2 (multi-tenant schema) tenant isolation, which is the framework's headline claim and
#                                which no test had ever built, let alone run.
#
# Requires: .NET 10 SDK, and MongoDB on localhost:27017 (docker compose up -d).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$REPO_ROOT/foundry-cli/src/Foundry.Cli/Foundry.Cli.csproj"
WORK_DIR="$(mktemp -d)"
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

# Starts the app in $1 on port $2 and waits for it to answer $3. Fails with the log if it never
# comes up: a silent timeout here would be indistinguishable from a crash, which is the whole point.
start_app() {
  local app_dir="$1" port="$2" probe_route="$3" logfile="$4"
  local app_dll="$app_dir/bin/Debug/net10.0/$(basename "$app_dir").dll"

  [[ -f "$app_dll" ]] || fail "no built application at $app_dll"

  # Refuse to start against an occupied port.
  #
  # This script used to leak its own applications and then test them. `dotnet run` starts the app as
  # a *child*, so killing the pid this script held stopped the launcher and left the application
  # running; the next run failed to bind, and its readiness probe was answered by the previous run's
  # process. Every assertion then ran against a binary built before the change under test -- a
  # harness reporting success for something it had not exercised, which is the exact failure this
  # whole script exists to catch.
  #
  # Fixed twice over: the port is checked here, and the application is launched directly below so the
  # pid is the application's own.
  if (exec 3<>"/dev/tcp/localhost/$port") 2>/dev/null; then
    exec 3<&- 2>/dev/null || true
    fail "port $port is already in use, so this run would test whatever is already listening there."
  fi

  # `exec` replaces the subshell with the application, so APP_PID is the application itself and the
  # `cd` still gives it the content root it needs for api-manifest.json and appsettings.
  ( cd "$app_dir" && exec dotnet "$app_dll" --urls "http://localhost:$port" ) >"$logfile" 2>&1 &
  APP_PID=$!

  # Readiness is "answers with the caller's identity applied", not merely "listens". An
  # unauthenticated probe would report ready on a 401 and prove nothing about the token chain.
  local waited=0
  until curl -fsS -o /dev/null ${AUTH[@]+"${AUTH[@]}"} "http://localhost:$port$probe_route" 2>/dev/null; do
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

# Credentials applied to every request. Set with `authenticate_as`, cleared with `authenticate_as_nobody`.
AUTH=()

# Emits the HTTP status of a request, with the body left in $WORK_DIR/body.json for inspection.
# Every assertion in this script goes through here so that a failure can report what came back
# rather than only that a number differed.
status() { curl -s -o "$WORK_DIR/body.json" -w '%{http_code}' ${AUTH[@]+"${AUTH[@]}"} "$@"; }

expect_status() {
  local expected="$1" description="$2"; shift 2
  local code
  code=$(status "$@")
  if [[ "$code" != "$expected" ]]; then
    echo "  response body: $(head -c 400 "$WORK_DIR/body.json")" >&2
    fail "$description: expected $expected, got $code"
  fi
  pass "$description -> $expected"
}

json_field() {
  python3 -c '
import json, sys
with open(sys.argv[1]) as handle:
    document = json.load(handle)
for key in sys.argv[2:]:
    if key in document:
        print(document[key])
        break
' "$@"
}

# Number of elements in a JSON array response, whatever the paging envelope happens to be.
json_count() {
  python3 -c '
import json, sys
with open(sys.argv[1]) as handle:
    payload = json.load(handle)
if isinstance(payload, dict):
    for key in ("items", "Items", "data", "Data"):
        if key in payload:
            payload = payload[key]
            break
print(len(payload) if isinstance(payload, list) else -1)
' "$1"
}

# ─── Identity ────────────────────────────────────────────────────────────────
#
# Generated endpoints require an authenticated caller, so the whole script runs as somebody. The
# tokens below are real HS256 JWTs, minted here and validated by the application: nothing about the
# authentication path is stubbed, which is the point. The key is supplied through the environment
# rather than appsettings, which is also the shape a deployment should use.
export Authentication__Jwt__SigningKey="foundry-runtime-smoke-test-signing-key-0123456789"
export Authentication__Jwt__Issuer="foundry-smoke-test"
export Authentication__Jwt__Audience="foundry-smoke-test"

# Mints a token: subject, comma-separated roles, tenant.
mint_token() {
  python3 - "$Authentication__Jwt__SigningKey" "$Authentication__Jwt__Issuer" \
            "$Authentication__Jwt__Audience" "$1" "${2:-}" "${3:-}" <<'PYTHON'
import base64, hashlib, hmac, json, sys, time

key, issuer, audience, subject, roles, tenant = sys.argv[1:7]

def b64(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

now = int(time.time())
payload = {
    "sub": subject,
    "iss": issuer,
    "aud": audience,
    "iat": now,
    "nbf": now,
    "exp": now + 1800,
}
if roles:
    payload["role"] = roles.split(",")
if tenant:
    payload["tenant_id"] = tenant

signing_input = "{}.{}".format(
    b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode()),
    b64(json.dumps(payload, separators=(",", ":")).encode()),
)
signature = hmac.new(key.encode(), signing_input.encode(), hashlib.sha256).digest()
print("{}.{}".format(signing_input, b64(signature)))
PYTHON
}

authenticate_as() { AUTH=(-H "Authorization: Bearer $1"); }
authenticate_as_nobody() { AUTH=(); }

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

log "Rebuilding the endpoint analyser"
# The scaffolded project references the analyser project and builds it in Debug, while everything
# else here builds Release. Building it explicitly, in the configuration the scaffolded project will
# use, keeps "what runs" and "what the generator currently emits" the same thing.
#
# This was originally added on the theory that a stale Debug analyser had caused a fix to appear not
# to take effect. That diagnosis was wrong -- the cause was a leaked application from an earlier run
# still holding the port (see start_app). Kept anyway: it is one cheap build, and it removes a real
# ambiguity about which copy of the generator produced the code under test.
dotnet build "$REPO_ROOT/foundry-api/src/Foundry.Api.SourceGenerators/Foundry.Api.SourceGenerators.csproj" \
  -v q --nologo --no-incremental || fail "the endpoint analyser does not build"
pass "endpoint analyser rebuilt"

# ─────────────────────────────────────────────────────────────────────────────
# Phase 1 -- the CRUD contract, from the default schema.
# ─────────────────────────────────────────────────────────────────────────────

APP_NAME="SmokeTestApp"
APP_DIR="$WORK_DIR/$APP_NAME"
PORT_A=5310
PORT_B=5311
BASE="http://localhost:$PORT_A"

log "Scaffolding a project with 'foundry new'"
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
authenticate_as "$(mint_token smoke-operator Admin)"
start_app "$APP_DIR" "$PORT_A" "/api/customers" "$WORK_DIR/app-a.log"
pass "application started and answered on /api/customers"

# ── Authentication ──────────────────────────────────────────────────────────
#
# The roles a schema declares were previously written into each endpoint's OpenAPI description and
# nowhere else. Every generated endpoint was anonymous while its own documentation stated the roles
# it required and advertised the 401 and 403 it could never return. These assert the door is locked
# before anything else asserts what is behind it.
log "An unauthenticated caller is refused"
authenticate_as_nobody
for route in customers orders; do
  expect_status 401 "GET /api/$route with no token" "$BASE/api/$route"
done
expect_status 401 "POST /api/customers with no token" \
  -X POST "$BASE/api/customers" -H 'Content-Type: application/json' \
  -d '{"fullName":"Anonymous","email":"anon@example.com"}'

log "A token this API did not issue is refused"
# Correctly formed, correctly signed -- with the wrong key. Accepting it would mean signature
# validation is not actually happening.
FORGED=$(Authentication__Jwt__SigningKey="an-attackers-own-signing-key-0123456789xyz" \
  mint_token attacker Admin)
authenticate_as "$FORGED"
expect_status 401 "GET /api/customers with a token signed by another key" "$BASE/api/customers"

log "Both entities are routed for an authenticated caller"
authenticate_as "$(mint_token smoke-operator Admin)"
for route in customers orders; do
  expect_status 200 "GET /api/$route" "$BASE/api/$route"
done

log "Creating a record"
expect_status 201 "POST /api/customers" \
  -X POST "$BASE/api/customers" \
  -H 'Content-Type: application/json' \
  -d '{"fullName":"Smoke Test","email":"smoke@example.com"}'

ENTITY_ID=$(json_field "$WORK_DIR/body.json" Id id)
[[ -n "$ENTITY_ID" ]] || fail "the created record has no id in the response body"
pass "created id $ENTITY_ID"

log "Reading it back in the same process"
expect_status 200 "GET /api/customers/$ENTITY_ID" "$BASE/api/customers/$ENTITY_ID"

VERSION=$(json_field "$WORK_DIR/body.json" Version version)
[[ "$VERSION" == "1" ]] || fail "a newly created record should be at version 1, got '$VERSION'"
pass "the record is at version 1"

# ── Validation ──────────────────────────────────────────────────────────────
#
# FullName and Email are declared Required in the schema. A create missing one must be refused:
# accepting it would persist a record the domain model says cannot exist.
log "A create that violates the schema is refused"
expect_status 400 "POST /api/customers with no fullName" \
  -X POST "$BASE/api/customers" \
  -H 'Content-Type: application/json' \
  -d '{"email":"missing-name@example.com"}'

# ── Filtering ───────────────────────────────────────────────────────────────
#
# The consequential direction is *widening*: a filter the server accepted and then quietly
# discarded returns a 200 with more rows than were asked for, and nothing records that it
# happened. Both halves are asserted -- that a good filter narrows, and that a bad one is
# refused rather than dropped.
log "Filters narrow, and unusable filters are refused"
expect_status 200 "GET /api/customers?FullName=Smoke%20Test" "$BASE/api/customers?FullName=Smoke%20Test"
[[ "$(json_count "$WORK_DIR/body.json")" -ge 1 ]] || fail "a filter matching the created record returned nothing"
pass "a matching filter returns the record"

expect_status 200 "GET /api/customers?FullName=NoSuchCustomer" "$BASE/api/customers?FullName=NoSuchCustomer"
[[ "$(json_count "$WORK_DIR/body.json")" == "0" ]] || fail "a filter matching nothing still returned rows -- the filter was dropped"
pass "a non-matching filter returns nothing"

# Malformed `criteria` used to hit a generated `} catch {}` and leave the filter null, so the
# endpoint ran the query unfiltered and answered 200 with the whole collection. This asserts the
# generated code, which no unit test can reach.
expect_status 400 "GET /api/customers with malformed criteria" \
  --get "$BASE/api/customers" --data-urlencode 'criteria={not json'

# ── Update and optimistic concurrency ───────────────────────────────────────
log "Updating the record"
expect_status 200 "PUT /api/customers/$ENTITY_ID" \
  -X PUT "$BASE/api/customers/$ENTITY_ID" \
  -H 'Content-Type: application/json' \
  -d "{\"id\":\"$ENTITY_ID\",\"fullName\":\"Smoke Test Renamed\",\"email\":\"smoke@example.com\",\"version\":1}"

expect_status 200 "GET after update" "$BASE/api/customers/$ENTITY_ID"
grep -q 'Smoke Test Renamed' "$WORK_DIR/body.json" || fail "the update did not change the stored record"
NEW_VERSION=$(json_field "$WORK_DIR/body.json" Version version)
[[ "$NEW_VERSION" == "2" ]] || fail "expected version 2 after one update, got '$NEW_VERSION'"
pass "the update is visible and the version advanced to 2"

# Re-sending version 1 is exactly what a client with a stale read does. Silently applying it is
# the lost update the version field exists to prevent, so the conflict has to reach the caller.
log "A stale write loses the concurrency race"
expect_status 409 "PUT with a stale version" \
  -X PUT "$BASE/api/customers/$ENTITY_ID" \
  -H 'Content-Type: application/json' \
  -d "{\"id\":\"$ENTITY_ID\",\"fullName\":\"Written From A Stale Read\",\"email\":\"smoke@example.com\",\"version\":1}"

expect_status 200 "GET after the rejected write" "$BASE/api/customers/$ENTITY_ID"
grep -q 'Written From A Stale Read' "$WORK_DIR/body.json" \
  && fail "the rejected write was applied anyway -- the 409 did not prevent the update"
pass "the rejected write left the record untouched"

# ── Durability ──────────────────────────────────────────────────────────────
log "Restarting the process"
stop_app
start_app "$APP_DIR" "$PORT_B" "/api/customers" "$WORK_DIR/app-b.log"
pass "application restarted"

log "The record survived the restart"
expect_status 200 "GET after restart" "http://localhost:$PORT_B/api/customers/$ENTITY_ID"
grep -q 'smoke@example.com' "$WORK_DIR/body.json" \
  || fail "the record read back after restart does not contain the value that was written"
grep -q 'Smoke Test Renamed' "$WORK_DIR/body.json" \
  || fail "the update did not survive the restart"
pass "the record and its update were served from MongoDB by a different process"

# ── Soft delete ─────────────────────────────────────────────────────────────
#
# Customer sets softDelete, so a delete must hide the row from reads while leaving it on disk.
# Asserted through the API: gone from GET by id and from the collection listing.
log "Deleting the record"
BASE="http://localhost:$PORT_B"
expect_status 204 "DELETE /api/customers/$ENTITY_ID" -X DELETE "$BASE/api/customers/$ENTITY_ID"
expect_status 404 "GET after delete" "$BASE/api/customers/$ENTITY_ID"

expect_status 200 "GET list after delete" "$BASE/api/customers?FullName=Smoke%20Test%20Renamed"
[[ "$(json_count "$WORK_DIR/body.json")" == "0" ]] \
  || fail "a soft-deleted record is still listed"
pass "the deleted record is hidden from reads"

stop_app

# ─────────────────────────────────────────────────────────────────────────────
# Phase 2 -- tenant isolation.
#
# This is the framework's headline claim and, until this phase existed, nothing had ever built a
# multi-tenant schema. Doing so failed to compile (CS8854: the emitted tenant key was `init`,
# and IMultiTenant declares `set`), which means no multi-tenant Foundry application had ever
# run. Behind that were three more breaks that only a running application can show: nothing
# resolved the tenant for a request, nothing stamped it on write, and the repository's list path
# never filtered by it.
# ─────────────────────────────────────────────────────────────────────────────

TENANT_APP="TenantSmokeTestApp"
TENANT_DIR="$WORK_DIR/$TENANT_APP"
PORT_C=5312
BASE="http://localhost:$PORT_C"

log "Scaffolding a multi-tenant project"
cat > "$WORK_DIR/tenant-schema.json" <<'SCHEMA'
{
  "Namespace": "TenantSmokeTestApp.Domain",
  "Entities": [
    {
      "Name": "Invoice",
      "multiTenant": true,
      "Properties": [
        { "Name": "Id", "Type": "ObjectId", "IsKey": true },
        { "Name": "TenantId", "Type": "string", "isTenantKey": true },
        { "Name": "Reference", "Type": "string", "Attributes": ["Required"] }
      ],
      "ApiEnabledMethods": ["GET", "POST", "GET_BY_ID", "PUT", "DELETE"],
      "ApiRoles": { "DELETE": ["Admin"] }
    }
  ]
}
SCHEMA

( cd "$WORK_DIR" && dotnet "$CLI_DLL" new "$TENANT_APP" --schema "$WORK_DIR/tenant-schema.json" ) \
  || fail "'foundry new' exited non-zero for the multi-tenant schema"
pass "multi-tenant project scaffolded"

log "Building the multi-tenant project"
( cd "$TENANT_DIR" && dotnet build -v q --nologo ) \
  || fail "a multi-tenant project does not compile"
pass "multi-tenant project compiles"

log "Booting the multi-tenant application"
# Tenancy now travels in the caller's token rather than a header they set themselves. That is the
# production path, and it is the one worth proving: a header is caller-assertable, a signed claim
# is not.
ACME_ADMIN=$(mint_token acme-admin Admin acme)
GLOBEX_ADMIN=$(mint_token globex-admin Admin globex)
ACME_CLERK=$(mint_token acme-clerk Clerk acme)

authenticate_as "$ACME_ADMIN"
start_app "$TENANT_DIR" "$PORT_C" "/api/invoices" "$WORK_DIR/app-c.log"
pass "multi-tenant application started"

log "Each tenant's write is stamped with its own tenant"
expect_status 201 "POST as tenant acme" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' \
  -d '{"reference":"ACME-001"}'
ACME_ID=$(json_field "$WORK_DIR/body.json" Id id)
ACME_TENANT=$(json_field "$WORK_DIR/body.json" TenantId tenantId)
[[ "$ACME_TENANT" == "acme" ]] || fail "the stored tenant was '$ACME_TENANT', expected 'acme'"
pass "acme's invoice is stamped acme from the token claim"

authenticate_as "$GLOBEX_ADMIN"
expect_status 201 "POST as tenant globex" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' \
  -d '{"reference":"GLOBEX-001"}'
GLOBEX_ID=$(json_field "$WORK_DIR/body.json" Id id)

# The tenant comes from the server's context, never from the body.
log "A caller cannot write into another tenant by naming it"
authenticate_as "$ACME_ADMIN"
expect_status 201 "POST as acme claiming to be globex in the body" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' \
  -d '{"reference":"FORGED","tenantId":"globex"}'
FORGED_TENANT=$(json_field "$WORK_DIR/body.json" TenantId tenantId)
[[ "$FORGED_TENANT" == "acme" ]] \
  || fail "a request body set the tenant to '$FORGED_TENANT' -- a caller can write into another tenant"
pass "the body's tenant was overwritten with the caller's own"

# The header used to be read before the token claim, so an authenticated caller could override the
# tenant their own token asserted simply by setting one.
log "A tenant header cannot override the tenant in the token"
expect_status 201 "POST as acme sending X-Tenant-ID: globex" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' \
  -H 'X-Tenant-ID: globex' -d '{"reference":"HEADER-OVERRIDE"}'
HEADER_TENANT=$(json_field "$WORK_DIR/body.json" TenantId tenantId)
[[ "$HEADER_TENANT" == "acme" ]] \
  || fail "the X-Tenant-ID header set the tenant to '$HEADER_TENANT' -- it outranks the signed claim"
pass "the signed claim won over the header"

# The failure this phase exists for. The list endpoint runs through FindManyAsync, which took the
# expression overload of the repository's read filter -- the one that applied soft delete and not
# the tenant. Every tenant saw every other tenant's rows, with a 200.
log "A list request sees only its own tenant's rows"
expect_status 200 "GET as acme" "$BASE/api/invoices"
grep -q 'GLOBEX-001' "$WORK_DIR/body.json" \
  && fail "acme's list contains globex's invoice -- tenant isolation is not applied to reads"
grep -q 'ACME-001' "$WORK_DIR/body.json" \
  || fail "acme's list is missing acme's own invoice"
pass "acme sees ACME-001 and not GLOBEX-001"

authenticate_as "$GLOBEX_ADMIN"
expect_status 200 "GET as globex" "$BASE/api/invoices"
grep -q 'ACME-001' "$WORK_DIR/body.json" \
  && fail "globex's list contains acme's invoice -- tenant isolation is not applied to reads"
pass "globex sees only its own invoice"

# An id is not a secret: it is handed out in every Location header and list response. Reads were
# tenant-scoped; writes addressed by id were not.
log "A known id from another tenant is not reachable"
authenticate_as "$ACME_ADMIN"
expect_status 404 "GET globex's invoice as acme" "$BASE/api/invoices/$GLOBEX_ID"
expect_status 200 "GET acme's invoice as acme" "$BASE/api/invoices/$ACME_ID"

authenticate_as "$GLOBEX_ADMIN"
expect_status 404 "GET acme's invoice as globex" "$BASE/api/invoices/$ACME_ID"

log "A cross-tenant write does not take effect"
# The update is a whole-document replace, so an unscoped one would both overwrite another
# tenant's row and move it between tenants.
authenticate_as "$ACME_ADMIN"
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X PUT "$BASE/api/invoices/$GLOBEX_ID" \
  -H 'Content-Type: application/json' \
  -d "{\"id\":\"$GLOBEX_ID\",\"reference\":\"STOLEN\",\"version\":1}" || true

authenticate_as "$GLOBEX_ADMIN"
expect_status 200 "GET globex's invoice as globex" "$BASE/api/invoices/$GLOBEX_ID"
grep -q 'STOLEN' "$WORK_DIR/body.json" \
  && fail "acme overwrote globex's invoice -- writes addressed by id are not tenant-scoped"
grep -q 'GLOBEX-001' "$WORK_DIR/body.json" \
  || fail "globex's invoice no longer holds its own value"
pass "globex's invoice is untouched"

# Issued with Admin, so the delete is authorised and only tenancy can stop it. A Clerk token here
# would pass for the wrong reason.
authenticate_as "$ACME_ADMIN"
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X DELETE "$BASE/api/invoices/$GLOBEX_ID" || true
authenticate_as "$GLOBEX_ADMIN"
expect_status 200 "globex's invoice survives a delete issued by acme" "$BASE/api/invoices/$GLOBEX_ID"
pass "a cross-tenant delete did not remove the row"

# ── Declared roles ──────────────────────────────────────────────────────────
#
# The schema declares "ApiRoles": { "DELETE": ["Admin"] }. That declaration used to reach the
# endpoint's OpenAPI description and nothing else.
log "A declared role is enforced, not just documented"
authenticate_as "$ACME_CLERK"
expect_status 403 "DELETE as Clerk, where the schema requires Admin" \
  -X DELETE "$BASE/api/invoices/$ACME_ID"

expect_status 200 "the invoice a Clerk could not delete is still there" "$BASE/api/invoices/$ACME_ID"
pass "the refused delete did not happen"

# A role the schema did not name must not open the door either, however privileged it sounds.
authenticate_as "$(mint_token acme-super SuperUser,Owner acme)"
expect_status 403 "DELETE with roles the schema never named" \
  -X DELETE "$BASE/api/invoices/$ACME_ID"

log "The declared role does grant access"
# Enforcement that refuses everyone is not enforcement -- it is an outage.
authenticate_as "$ACME_ADMIN"
expect_status 204 "DELETE as Admin" -X DELETE "$BASE/api/invoices/$ACME_ID"
expect_status 404 "GET after the authorised delete" "$BASE/api/invoices/$ACME_ID"

# A multi-tenant row with no tenant is invisible to every tenant once isolation is on, and
# visible to all of them until then. The write is refused rather than silently orphaned.
#
# 500 and not 400: an application that declares multi-tenant entities and cannot resolve a tenant
# is misconfigured. The caller is authenticated and their token simply carries no tenant, which is
# a deployment mistake rather than a malformed request.
log "A multi-tenant write with no tenant is refused"
authenticate_as "$(mint_token tenantless Admin)"
expect_status 500 "POST with a token carrying no tenant" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' -d '{"reference":"NO-TENANT"}'

stop_app

printf '\n\033[32mAll runtime checks passed.\033[0m\n'
printf 'A scaffolded app boots, refuses anonymous and forged tokens, enforces the roles its schema\n'
printf 'declares, validates, filters, updates under optimistic concurrency, soft-deletes, survives a\n'
printf 'restart, and keeps tenants apart on reads and writes using the tenant in the caller token.\n'
