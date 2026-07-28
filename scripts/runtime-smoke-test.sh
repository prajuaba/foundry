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
#   Phase 1 (default schema)  authentication, and the CRUD contract -- create, read, update,
#                             delete, filter, validate, concurrency, durability across a restart.
#   Phase 2 (rich schema)     the access-control and behaviour claims, none of which any test had
#                             ever built, let alone run: tenant isolation, row-level ownership,
#                             declared roles, and workflow transitions.
#
# Every request carries a real HS256 JWT minted by this script, so the authentication path is
# exercised rather than stubbed.
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

  # Best effort: the run is isolated by database name, so a leftover one is untidy rather than
  # harmful, and a missing mongosh must not turn a passing run into a failing one.
  if [[ -n "${MONGODB_DATABASE:-}" ]] && command -v mongosh >/dev/null 2>&1; then
    mongosh --quiet --eval "db.getSiblingDB('$MONGODB_DATABASE').dropDatabase()" >/dev/null 2>&1 || true
  fi
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
# A database per run. The scaffolded projects have fixed names, so without this every run shared one
# set of collections with every previous run -- and a run that failed part-way left rows behind that
# the next run counted. That is a harness reporting a defect it created itself, which is worse than
# no harness.
export MONGODB_DATABASE="FoundrySmoke_$(date +%s)_$$"

export Authentication__Jwt__SigningKey="foundry-runtime-smoke-test-signing-key-0123456789"
export Authentication__Jwt__Issuer="foundry-smoke-test"
export Authentication__Jwt__Audience="foundry-smoke-test"

# Mints a token: subject, comma-separated roles, tenant, comma-separated groups.
mint_token() {
  python3 - "$Authentication__Jwt__SigningKey" "$Authentication__Jwt__Issuer" \
            "$Authentication__Jwt__Audience" "$1" "${2:-}" "${3:-}" "${4:-}" <<'PYTHON'
import base64, hashlib, hmac, json, sys, time

key, issuer, audience, subject, roles, tenant, groups = sys.argv[1:8]

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
if groups:
    payload["groups"] = groups.split(",")

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
# The default schema declares Email with "MaskEmail", so the value comes back masked to a caller
# without the view:pii scope. Durability is asserted on a property that is not masked, and the
# masked one is checked for its domain -- present proves the row was persisted and read back,
# masked proves the declaration is enforced rather than merely recorded.
grep -q 'Smoke Test' "$WORK_DIR/body.json" \
  || fail "the record read back after restart does not contain the value that was written"
grep -q '@example.com' "$WORK_DIR/body.json" \
  || fail "the record read back after restart lost its email entirely"
grep -q 'smoke@example.com' "$WORK_DIR/body.json" \
  && fail "the email came back unmasked, though the schema declares MaskEmail"
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
        { "Name": "Reference", "Type": "string", "Attributes": ["Required"] },
        { "Name": "Amount", "Type": "decimal" }
      ],
      "ApiEnabledMethods": ["GET", "POST", "GET_BY_ID", "PUT", "DELETE"],
      "ApiRoles": { "DELETE": ["Admin"] }
    },
    {
      "Name": "Note",
      "multiTenant": true,
      "ownerScoped": true,
      "ownerExemptRoles": ["Supervisor"],
      "ownerReadExemptRoles": ["Auditor"],
      "Properties": [
        { "Name": "Id", "Type": "ObjectId", "IsKey": true },
        { "Name": "TenantId", "Type": "string", "isTenantKey": true },
        { "Name": "OwnerId", "Type": "string", "isOwnerKey": true },
        { "Name": "SharedWith", "Type": "List<string>", "isSharedWithKey": true },
        { "Name": "ContactEmail", "Type": "string", "Attributes": ["MaskEmail"] },
        { "Name": "Body", "Type": "string", "Attributes": ["Required"] }
      ],
      "ApiEnabledMethods": ["GET", "POST", "GET_BY_ID", "PUT", "DELETE"]
    }
  ],
  "Workflows": [
    {
      "Id": "invoice-approval",
      "Name": "Invoice Approval",
      "Entity": "Invoice",
      "Version": "1.0",
      "IsActive": true,
      "States": [
        { "Name": "Draft", "IsInitial": true },
        { "Name": "Submitted" },
        { "Name": "Approved", "IsFinal": true }
      ],
      "Transitions": [
        { "Id": "submit", "Name": "Submit", "FromState": "Draft", "ToState": "Submitted", "Trigger": "SubmitInvoice" },
        { "Id": "approve", "Name": "Approve", "FromState": "Submitted", "ToState": "Approved", "Trigger": "ApproveInvoice", "RequiredRoles": ["Approver"] },
        { "Id": "route", "Name": "Route", "FromState": "Draft", "ToState": "amount_gate", "Trigger": "RouteInvoice" }
      ],
      "ChoiceNodes": [
        {
          "Id": "amount_gate",
          "Name": "Amount Gate",
          "defaultState": "Submitted",
          "Branches": [
            {
              "TargetState": "Approved",
              "Condition": { "Property": "Amount", "Operator": "greaterthan", "Value": "500" }
            }
          ]
        }
      ]
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

# ── Row-level ownership ─────────────────────────────────────────────────────
#
# Roles decide whether a caller may use an endpoint. Ownership decides which rows they see through
# it. Without it any caller holding a role reached every row in their tenant, which is adequate for
# a back-office tool and not for anything where users hold their own records.
#
# Note declares "ownerScoped": true with "ownerExemptRoles": ["Supervisor"], and is multi-tenant, so
# this also exercises the interaction that matters most: exemption must lift the owner filter and
# never the tenant filter.
ALICE=$(mint_token alice "" acme)
BOB=$(mint_token bob "" acme)
ACME_SUPERVISOR=$(mint_token acme-supervisor Supervisor acme)
GLOBEX_SUPERVISOR=$(mint_token globex-supervisor Supervisor globex)

log "A row is stamped with the caller who created it"
authenticate_as "$ALICE"
expect_status 201 "POST /api/notes as alice" \
  -X POST "$BASE/api/notes" -H 'Content-Type: application/json' -d '{"body":"ALICE-NOTE"}'
ALICE_NOTE=$(json_field "$WORK_DIR/body.json" Id id)
NOTE_OWNER=$(json_field "$WORK_DIR/body.json" OwnerId ownerId)
[[ "$NOTE_OWNER" == "alice" ]] || fail "the note's owner was '$NOTE_OWNER', expected 'alice'"
pass "alice's note is stamped alice from the token's sub claim"

authenticate_as "$BOB"
expect_status 201 "POST /api/notes as bob" \
  -X POST "$BASE/api/notes" -H 'Content-Type: application/json' -d '{"body":"BOB-NOTE"}'
BOB_NOTE=$(json_field "$WORK_DIR/body.json" Id id)

log "A caller cannot create a row owned by somebody else"
authenticate_as "$ALICE"
expect_status 201 "POST as alice claiming to be bob in the body" \
  -X POST "$BASE/api/notes" -H 'Content-Type: application/json' \
  -d '{"body":"FORGED-OWNER","ownerId":"bob"}'
FORGED_OWNER=$(json_field "$WORK_DIR/body.json" OwnerId ownerId)
[[ "$FORGED_OWNER" == "alice" ]] \
  || fail "a request body set the owner to '$FORGED_OWNER' -- a caller can create rows owned by others"
pass "the body's owner was overwritten with the caller's own"

log "A list request returns only the caller's own rows"
expect_status 200 "GET /api/notes as alice" "$BASE/api/notes"
grep -q 'BOB-NOTE' "$WORK_DIR/body.json" \
  && fail "alice's list contains bob's note -- ownership is not applied to reads"
grep -q 'ALICE-NOTE' "$WORK_DIR/body.json" \
  || fail "alice's list is missing her own note"
pass "alice sees ALICE-NOTE and not BOB-NOTE"

log "Another caller's row is not reachable by id"
expect_status 404 "GET bob's note as alice" "$BASE/api/notes/$BOB_NOTE"
expect_status 200 "GET alice's note as alice" "$BASE/api/notes/$ALICE_NOTE"

log "A write against another caller's row does not take effect"
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X PUT "$BASE/api/notes/$BOB_NOTE" \
  -H 'Content-Type: application/json' \
  -d "{\"id\":\"$BOB_NOTE\",\"body\":\"STOLEN\",\"version\":1}" || true
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X DELETE "$BASE/api/notes/$BOB_NOTE" || true

authenticate_as "$BOB"
expect_status 200 "GET bob's note as bob" "$BASE/api/notes/$BOB_NOTE"
grep -q 'STOLEN' "$WORK_DIR/body.json" \
  && fail "alice overwrote bob's note -- writes addressed by id are not owner-scoped"
grep -q 'BOB-NOTE' "$WORK_DIR/body.json" \
  || fail "bob's note no longer holds its own value"
pass "bob's note survived both a cross-owner update and a cross-owner delete"

log "An exempt role sees every row in its tenant"
# Supervisors and auditors are the reason ownership can be enforced by default rather than opted in.
authenticate_as "$ACME_SUPERVISOR"
expect_status 200 "GET /api/notes as an acme Supervisor" "$BASE/api/notes"
grep -q 'ALICE-NOTE' "$WORK_DIR/body.json" || fail "the supervisor cannot see alice's note"
grep -q 'BOB-NOTE' "$WORK_DIR/body.json" || fail "the supervisor cannot see bob's note"
pass "the acme supervisor sees both notes"

expect_status 200 "GET bob's note as an acme Supervisor" "$BASE/api/notes/$BOB_NOTE"

# The interaction that matters most. Exemption lifts the owner filter; if it lifted the tenant
# filter too, a supervisor in one tenant would become a supervisor of all of them.
log "An exempt role is still confined to its own tenant"
authenticate_as "$GLOBEX_SUPERVISOR"
expect_status 200 "GET /api/notes as a globex Supervisor" "$BASE/api/notes"
grep -q 'ALICE-NOTE' "$WORK_DIR/body.json" \
  && fail "a globex supervisor sees acme's notes -- exemption lifted the tenant filter"
grep -q 'BOB-NOTE' "$WORK_DIR/body.json" \
  && fail "a globex supervisor sees acme's notes -- exemption lifted the tenant filter"
pass "the globex supervisor sees none of acme's notes"

expect_status 404 "GET an acme note as a globex Supervisor" "$BASE/api/notes/$ALICE_NOTE"

# ── Grants: sharing, delegation and team scoping ────────────────────────────
#
# Ownership answered "this row belongs to one caller" and nothing past it, so sharing, delegation and
# team scoping had nowhere to be expressed and had to be written by hand in a business rule -- where
# nothing checked the rule reached every read path. They are one predicate: a row is visible to its
# owner and to any identity in SharedWith, and the caller's identities are their subject plus the
# groups their token carries.
CAROL=$(mint_token carol "" acme)
FINANCE_MEMBER=$(mint_token dave "" acme finance)
GLOBEX_FINANCE=$(mint_token globex-dave "" globex finance)

log "A row shared with a caller becomes visible to them"
authenticate_as "$ALICE"
expect_status 201 "POST a note shared with carol" \
  -X POST "$BASE/api/notes" -H 'Content-Type: application/json' \
  -d '{"body":"SHARED-WITH-CAROL","sharedWith":["carol"]}'
SHARED_NOTE=$(json_field "$WORK_DIR/body.json" Id id)

authenticate_as "$CAROL"
expect_status 200 "GET the shared note as carol" "$BASE/api/notes/$SHARED_NOTE"
expect_status 200 "GET /api/notes as carol" "$BASE/api/notes"
grep -q 'SHARED-WITH-CAROL' "$WORK_DIR/body.json" \
  || fail "carol's list is missing the note shared with her -- the list and by-id filters disagree"
grep -q 'ALICE-NOTE' "$WORK_DIR/body.json" \
  && fail "carol sees a note that was never shared with her"
pass "carol sees the shared note and nothing else of alice's"

# The half that matters. A grant that silently conferred write access would turn "let my colleague
# see this" into "let my colleague rewrite this".
log "A grant does not confer write access"
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X PUT "$BASE/api/notes/$SHARED_NOTE" \
  -H 'Content-Type: application/json' \
  -d "{\"id\":\"$SHARED_NOTE\",\"body\":\"REWRITTEN-BY-CAROL\",\"version\":1}" || true
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X DELETE "$BASE/api/notes/$SHARED_NOTE" || true

authenticate_as "$ALICE"
expect_status 200 "GET the shared note as its owner" "$BASE/api/notes/$SHARED_NOTE"
grep -q 'REWRITTEN-BY-CAROL' "$WORK_DIR/body.json" \
  && fail "a grantee rewrote a row shared with them -- a grant conferred write access"
grep -q 'SHARED-WITH-CAROL' "$WORK_DIR/body.json" \
  || fail "the shared note no longer holds its own value"
pass "the shared note survived a grantee's update and delete"

log "A row shared with a team is visible to its members"
expect_status 201 "POST a note shared with the finance group" \
  -X POST "$BASE/api/notes" -H 'Content-Type: application/json' \
  -d '{"body":"SHARED-WITH-FINANCE","sharedWith":["finance"]}'

authenticate_as "$FINANCE_MEMBER"
expect_status 200 "GET /api/notes as a finance group member" "$BASE/api/notes"
grep -q 'SHARED-WITH-FINANCE' "$WORK_DIR/body.json" \
  || fail "a group grant did not reach a member of that group"
grep -q 'SHARED-WITH-CAROL' "$WORK_DIR/body.json" \
  && fail "a group member sees a note shared only with an individual"
pass "the finance group sees the note granted to it and no other"

# A grant widens the owner filter and never the tenant filter, exactly as an exemption does.
log "A grant does not cross a tenant"
authenticate_as "$GLOBEX_FINANCE"
expect_status 200 "GET /api/notes as globex finance" "$BASE/api/notes"
grep -q 'SHARED-WITH-FINANCE' "$WORK_DIR/body.json" \
  && fail "a grant reached the same group in another tenant -- it was applied above the tenant filter"
pass "the same group in another tenant sees nothing"

# ── Read-only exemption ─────────────────────────────────────────────────────
#
# ownerExemptRoles is per entity, not per operation, so a role exempted for reads was exempted for
# updates and deletes too. Read-only oversight -- an auditor, a compliance reviewer -- could not be
# expressed at all.
ACME_AUDITOR=$(mint_token acme-auditor Auditor acme)

log "A read-only exempt role sees every row in its tenant"
authenticate_as "$ACME_AUDITOR"
expect_status 200 "GET /api/notes as an acme Auditor" "$BASE/api/notes"
grep -q 'ALICE-NOTE' "$WORK_DIR/body.json" || fail "the auditor cannot see alice's note"
grep -q 'BOB-NOTE' "$WORK_DIR/body.json" || fail "the auditor cannot see bob's note"
pass "the auditor sees rows belonging to every caller"

log "A read-only exempt role cannot change any of them"
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X PUT "$BASE/api/notes/$BOB_NOTE" \
  -H 'Content-Type: application/json' \
  -d "{\"id\":\"$BOB_NOTE\",\"body\":\"AUDITED\",\"version\":1}" || true
curl -s -o /dev/null ${AUTH[@]+"${AUTH[@]}"} -X DELETE "$BASE/api/notes/$BOB_NOTE" || true

authenticate_as "$BOB"
expect_status 200 "GET bob's note after the auditor's writes" "$BASE/api/notes/$BOB_NOTE"
grep -q 'AUDITED' "$WORK_DIR/body.json" \
  && fail "a read-only exempt role rewrote another caller's row"
grep -q 'BOB-NOTE' "$WORK_DIR/body.json" \
  || fail "bob's note no longer holds its own value"
pass "the auditor could read every row and change none"

# ── Field-level restriction ─────────────────────────────────────────────────
#
# Row filters decide which records come back; this decides what is left inside them. The masking
# machinery was written, unit-tested in isolation, and called by nothing -- so a property declared
# [SensitiveData(Protection = Mask)] came back in the clear on every transport. Applied in the
# repository, so one rule covers REST, GraphQL and the generated SDKs rather than three.
PII_READER=$(python3 - "$Authentication__Jwt__SigningKey" "$Authentication__Jwt__Issuer" \
                       "$Authentication__Jwt__Audience" <<'PYTHON'
import base64, hashlib, hmac, json, sys, time

key, issuer, audience = sys.argv[1:4]

def b64(raw: bytes) -> str:
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()

now = int(time.time())
payload = {
    # Alice again, so the only difference from the reads above is the scope. A different subject
    # would not see the row at all -- row filters run before field filters, and the 404 that
    # produces would look like a masking failure while proving nothing about masking.
    "sub": "alice", "iss": issuer, "aud": audience,
    "iat": now, "nbf": now, "exp": now + 1800,
    "tenant_id": "acme",
    # The scope that entitles a caller to unmasked reads. Everyone else sees the masked form.
    "scope": "view:pii",
}
signing_input = "{}.{}".format(
    b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode()),
    b64(json.dumps(payload, separators=(",", ":")).encode()),
)
signature = hmac.new(key.encode(), signing_input.encode(), hashlib.sha256).digest()
print("{}.{}".format(signing_input, b64(signature)))
PYTHON
)

log "A masked property is stored in full and returned masked"
authenticate_as "$ALICE"
expect_status 201 "POST a note carrying a masked property" \
  -X POST "$BASE/api/notes" -H 'Content-Type: application/json' \
  -d '{"body":"MASKING-NOTE","contactEmail":"john.doe@example.com"}'
MASKED_NOTE=$(json_field "$WORK_DIR/body.json" Id id)

expect_status 200 "GET the note as its owner" "$BASE/api/notes/$MASKED_NOTE"
grep -q 'john.doe@example.com' "$WORK_DIR/body.json" \
  && fail "a masked property came back in the clear -- the declaration protects nothing"
grep -q '@example.com' "$WORK_DIR/body.json" \
  || fail "the masked value lost its domain, so the mask is not the one the schema declared"
pass "the owner sees the masked form, with the domain preserved"

log "A list is masked as well as a read by id"
# The by-id and list paths have diverged before: the tenant filter was applied by one and not the
# other for as long as it existed. Masking one and not the other would be worse than masking
# neither, because the protection would read as present.
expect_status 200 "GET /api/notes as alice" "$BASE/api/notes"
grep -q 'john.doe@example.com' "$WORK_DIR/body.json" \
  && fail "the list path returned an unmasked value the by-id path masked"
pass "both read paths mask"

log "The same caller holding the view:pii scope sees the stored value"
authenticate_as "$PII_READER"
expect_status 200 "GET the note as a view:pii holder" "$BASE/api/notes/$MASKED_NOTE"
grep -q 'john.doe@example.com' "$WORK_DIR/body.json" \
  || fail "an entitled caller could not see the value, so it was masked in storage rather than on read"
pass "the entitled caller sees the value in full, proving it was stored intact"

# ── Workflow transitions ────────────────────────────────────────────────────
#
# A workflow declared in a schema used to be compiled, validated, and then go nowhere: the manifest
# never carried the definitions, the scaffolder never registered the engine, and no route could send
# a transition command. Every layer downstream was in place and waiting for a list that arrived
# empty. This drives one from a running application.
log "A new record starts outside the workflow"
authenticate_as "$ACME_ADMIN"
expect_status 201 "POST /api/invoices for the workflow" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' -d '{"reference":"WF-001"}'
WF_ID=$(json_field "$WORK_DIR/body.json" Id id)

expect_status 200 "GET the new invoice" "$BASE/api/invoices/$WF_ID"
WF_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
[[ -z "$WF_STATE" ]] \
  || fail "a new record already reports state '$WF_STATE'; it should not have entered the workflow yet"
pass "the invoice has no workflow state yet"

log "A transition advances the record and is persisted"
expect_status 200 "POST /api/invoices/transitions/submitinvoice" \
  -X POST "$BASE/api/invoices/transitions/submitinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$WF_ID\"}"

expect_status 200 "GET after the transition" "$BASE/api/invoices/$WF_ID"
WF_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
[[ "$WF_STATE" == "Submitted" ]] \
  || fail "expected state 'Submitted' after the transition, got '$WF_STATE'"
grep -q 'invoice-approval' "$WORK_DIR/body.json" \
  || fail "the record does not record which workflow it is in"
pass "the invoice advanced Draft -> Submitted and carries its workflow id"

# The state machine is the point: a transition whose source state does not match must be refused,
# not applied. Unmapped this surfaced as a bare 500 with the engine's explanation swallowed.
log "A transition from the wrong state is refused"
expect_status 409 "POST submitinvoice again, now that it is Submitted" \
  -X POST "$BASE/api/invoices/transitions/submitinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$WF_ID\"}"
grep -q 'Submitted' "$WORK_DIR/body.json" \
  || fail "the refusal does not say what state the record is actually in"
pass "the conflict names the current state"

expect_status 200 "GET after the refused transition" "$BASE/api/invoices/$WF_ID"
WF_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
[[ "$WF_STATE" == "Submitted" ]] || fail "the refused transition changed the state to '$WF_STATE'"
pass "the refused transition left the record where it was"

# The transition declares "RequiredRoles": ["Approver"], which reaches the endpoint as well as the
# workflow engine.
log "A transition's declared roles are enforced"
authenticate_as "$ACME_CLERK"
expect_status 403 "POST approveinvoice as Clerk" \
  -X POST "$BASE/api/invoices/transitions/approveinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$WF_ID\"}"

log "The declared role completes the workflow"
authenticate_as "$(mint_token acme-approver Approver acme)"
expect_status 200 "POST approveinvoice as Approver" \
  -X POST "$BASE/api/invoices/transitions/approveinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$WF_ID\"}"

authenticate_as "$ACME_ADMIN"
expect_status 200 "GET after approval" "$BASE/api/invoices/$WF_ID"
WF_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
[[ "$WF_STATE" == "Approved" ]] || fail "expected state 'Approved', got '$WF_STATE'"
pass "the invoice reached its final state"

# A transition is a write, so tenant isolation applies to it like any other.
log "A transition cannot be driven against another tenant's record"
authenticate_as "$GLOBEX_ADMIN"
code=$(status -X POST "$BASE/api/invoices/transitions/submitinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$WF_ID\"}")
[[ "$code" != "200" ]] \
  || fail "globex drove a transition on an acme invoice -- workflow writes are not tenant-scoped"
pass "a cross-tenant transition was refused with $code"

# ── Decision gates ──────────────────────────────────────────────────────────
#
# Choice nodes were emitted by the compiler, carried by the manifest, resolved by the behaviour --
# and never driven. Run, the resolution worked and the *unmatched* case did not: an unmatched gate
# with no declared default assigned the empty string as the record's state and saved it, leaving a
# document no transition matches, behind a 200 and a history entry naming "".
#
# The gate here routes to Approved above 500 and falls back to Submitted otherwise.
log "A decision gate routes on the branch whose condition holds"
authenticate_as "$ACME_ADMIN"
expect_status 201 "POST a high-value invoice" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' \
  -d '{"reference":"GATE-HIGH","amount":900}'
GATE_HIGH=$(json_field "$WORK_DIR/body.json" Id id)

expect_status 200 "POST /api/invoices/transitions/routeinvoice for the high-value invoice" \
  -X POST "$BASE/api/invoices/transitions/routeinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$GATE_HIGH\"}"

expect_status 200 "GET the high-value invoice" "$BASE/api/invoices/$GATE_HIGH"
GATE_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
[[ "$GATE_STATE" == "Approved" ]] \
  || fail "the gate routed a 900 invoice to '$GATE_STATE', expected 'Approved'"
pass "the gate routed on its condition to Approved"

log "A gate falls back to its declared default when no branch holds"
expect_status 201 "POST a low-value invoice" \
  -X POST "$BASE/api/invoices" -H 'Content-Type: application/json' \
  -d '{"reference":"GATE-LOW","amount":100}'
GATE_LOW=$(json_field "$WORK_DIR/body.json" Id id)

expect_status 200 "POST routeinvoice for the low-value invoice" \
  -X POST "$BASE/api/invoices/transitions/routeinvoice" \
  -H 'Content-Type: application/json' -d "{\"entityId\":\"$GATE_LOW\"}"

expect_status 200 "GET the low-value invoice" "$BASE/api/invoices/$GATE_LOW"
GATE_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
[[ "$GATE_STATE" == "Submitted" ]] \
  || fail "the gate routed a 100 invoice to '$GATE_STATE', expected the default 'Submitted'"
pass "the gate fell back to its declared default"

# The record must never land in the gate's own id, or in nothing at all.
log "A gate never leaves the record in a state no transition matches"
for gated in "$GATE_HIGH" "$GATE_LOW"; do
  expect_status 200 "GET $gated after routing" "$BASE/api/invoices/$gated"
  GATE_STATE=$(json_field "$WORK_DIR/body.json" CurrentState currentState)
  [[ -n "$GATE_STATE" ]] \
    || fail "the record was left with no state at all -- the empty-state defect"
  [[ "$GATE_STATE" != "amount_gate" ]] \
    || fail "the record was left holding the gate's id as though it were a state"
done
pass "both records hold real states"

# ── Workflow history ────────────────────────────────────────────────────────
#
# AppendActivityLogAsync wrote an entry for every transition -- who, when, from which state to which
# -- and nothing served it. For a regulated buyer the audit trail is the point, so a record that can
# be written and not read is half a feature. The two transitions above are what this reads back, so
# it proves the write and the read agree rather than proving either alone.
log "A record's transition history can be read back"
authenticate_as "$ACME_ADMIN"
expect_status 200 "GET /api/invoices/$WF_ID/history" "$BASE/api/invoices/$WF_ID/history"

HISTORY=$(python3 - "$WORK_DIR/body.json" <<'PYTHON'
import json, sys

entries = json.load(open(sys.argv[1]))
print(len(entries))
for entry in entries:
    print("{}:{}->{}:{}".format(
        entry["transitionId"], entry["fromState"], entry["toState"], entry["triggeredBy"]))
PYTHON
)

[[ "$(printf '%s\n' "$HISTORY" | head -1)" == "2" ]] \
  || fail "expected two transitions in the history, got: $HISTORY"
printf '%s\n' "$HISTORY" | grep -q 'Draft->Submitted' \
  || fail "the history does not record the first transition: $HISTORY"
printf '%s\n' "$HISTORY" | grep -q 'Submitted->Approved' \
  || fail "the history does not record the second transition: $HISTORY"
printf '%s\n' "$HISTORY" | grep -q 'acme-approver' \
  || fail "the history does not record who triggered the approval: $HISTORY"
pass "the history records both transitions, in order, with who triggered them"

# History is a read of the record, so it answers to the same isolation. WorkflowActivityLog is not
# itself tenant-scoped, so the endpoint loads the entity first and serves nothing if that fails --
# without which this would be a read path beside the generated endpoints with none of their filters.
log "Another tenant cannot read this record's history"
authenticate_as "$GLOBEX_ADMIN"
expect_status 404 "GET another tenant's history" "$BASE/api/invoices/$WF_ID/history"

log "An anonymous caller cannot read history"
authenticate_as_nobody
expect_status 401 "GET history with no token" "$BASE/api/invoices/$WF_ID/history"

# ── Real-time channels ──────────────────────────────────────────────────────
#
# The real-time channels carry AuditLogEntry notifications, and an AuditLogEntry carries
# PropertyDiffs -- the changed values. None of the three required a token: in an application where
# every generated CRUD endpoint answers 401, /realtime/sse returned 200 and streamed, and the
# SignalR hub negotiated. An anonymous client could watch every mutation in the system while being
# refused the endpoint that produced it.
log "The real-time channels refuse an anonymous client"
authenticate_as_nobody
expect_status 401 "GET /realtime/sse with no token" --max-time 10 "$BASE/realtime/sse"
expect_status 401 "POST /realtime/hub/negotiate with no token" \
  --max-time 10 -X POST "$BASE/realtime/hub/negotiate?negotiateVersion=1"
expect_status 401 "GET /realtime/ws with no token" --max-time 10 "$BASE/realtime/ws"

log "An authenticated client is accepted"
# 000 is curl's code for a transfer it cut short: SSE holds the connection open, so a timeout here
# means the stream was established. A 401 would have returned promptly with a status.
authenticate_as "$ACME_ADMIN"
sse_code=$(status --max-time 3 "$BASE/realtime/sse" || true)
[[ "$sse_code" == "200" || "$sse_code" == "000" ]] \
  || fail "an authenticated SSE connection was refused with $sse_code"
pass "SSE accepted an authenticated client"

expect_status 200 "POST /realtime/hub/negotiate as an authenticated caller" \
  --max-time 10 -X POST "$BASE/realtime/hub/negotiate?negotiateVersion=1"

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

# ── Exported specifications ─────────────────────────────────────────────────
#
# 'foundry export' had no test in any form, and all four formats exit 0 whatever they emit -- so
# "it works" had only ever meant "the process did not crash". Two of the four described an API that
# did not exist: OpenAPI and Postman composed /api/v1/{lowercase-singular} while the application
# serves /api/{plural}, and both emitted full CRUD for every entity regardless of what it declared.
#
# The unit tests compare the export against the manifest. This compares it against the running
# server, which is the only version of the claim that cannot be satisfied by two components agreeing
# on the same mistake.
log "The exported OpenAPI describes routes this application actually serves"
for format in openapi asyncapi postman mermaid; do
  ( cd "$WORK_DIR" && dotnet "$CLI_DLL" export -i "$WORK_DIR/tenant-schema.json" -f "$format" -o "$WORK_DIR/spec.$format" ) \
    >/dev/null 2>&1 || fail "'foundry export -f $format' exited non-zero"
  [[ -s "$WORK_DIR/spec.$format" ]] || fail "'foundry export -f $format' wrote an empty file"
done
pass "all four formats exported"

python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$WORK_DIR/spec.openapi" \
  || fail "the exported OpenAPI is not valid JSON"
python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$WORK_DIR/spec.asyncapi" \
  || fail "the exported AsyncAPI is not valid JSON"
python3 -c 'import json,sys; json.load(open(sys.argv[1]))' "$WORK_DIR/spec.postman" \
  || fail "the exported Postman collection is not valid JSON"
grep -q '^classDiagram' "$WORK_DIR/spec.mermaid" || fail "the exported Mermaid is not a class diagram"
pass "each export parses as the format it claims to be"

COLLECTION_PATHS=$(python3 - "$WORK_DIR/spec.openapi" <<'PYTHON'
import json, sys

spec = json.load(open(sys.argv[1]))
for path, operations in spec["paths"].items():
    if "{id}" not in path and "get" in operations:
        print(path)
PYTHON
)

[[ -n "$COLLECTION_PATHS" ]] || fail "the exported OpenAPI documents no readable path at all"

authenticate_as "$ACME_ADMIN"
while read -r documented; do
  [[ -n "$documented" ]] || continue
  code=$(status "$BASE$documented")
  [[ "$code" != "404" ]] \
    || fail "the OpenAPI export documents GET $documented, which this application answers with 404"
  pass "GET $documented is documented and served ($code)"
done <<< "$COLLECTION_PATHS"

stop_app

printf '\n\033[32mAll runtime checks passed.\033[0m\n'
printf 'A scaffolded app boots, refuses anonymous and forged tokens, enforces the roles its schema\n'
printf 'declares, validates, filters, updates under optimistic concurrency, soft-deletes, survives a\n'
printf 'restart, and keeps tenants apart on reads and writes using the tenant in the caller token.\n'
printf 'Its exported specifications describe routes that server actually answers, a workflow\n'
printf 'record'"'"'s transition history reads back what the transitions wrote, and a property declared\n'
printf 'sensitive is stored in full and returned masked to everyone without the scope to see it.\n'
