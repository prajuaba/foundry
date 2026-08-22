#!/usr/bin/env bash
#
# Refuses to let a release be tagged while Foundry's own CI is red.
#
# This exists because versions 2.5.0 through 2.8.0 were all tagged while `main` was
# failing. The reason was not carelessness about CI in general -- it was that the
# consumer application's pipeline was the one being watched, and a consumer going
# green says nothing about the framework it consumes. RELEASING.md now documents
# the check; this script is the check, because a step that only exists as prose is
# the kind of step that gets skipped, which is the entire thesis of this project.
#
# Usage:  scripts/preflight-release.sh [commit-ish]
#   Defaults to HEAD -- the commit you are about to tag.
#
# Exit codes:
#   0  every job green on the named commit; safe to tag
#   1  CI is red, still running, or green on a different commit
#   2  the check could not run at all (no gh, not authenticated, no remote)

set -euo pipefail

readonly WORKFLOW="CI"
readonly BRANCH="main"

# Every job in .github/workflows/ci.yml. Listed explicitly rather than derived from
# the run, so that a job silently disappearing from the workflow is itself a failure
# rather than a shorter list that still passes.
readonly REQUIRED_JOBS=(
  "Build and test"
  "Outbox round trip"
  "Runtime smoke test"
  "Distro binary"
  "Schema gates"
  "VS Code extension"
  "Studio tests and typecheck"
)

die()  { printf '\033[31m[blocked]\033[0m %s\n' "$1" >&2; exit "${2:-1}"; }
ok()   { printf '\033[32m  ok\033[0m      %s\n' "$1"; }
bad()  { printf '\033[31m  %-7s\033[0m %s\n' "$2" "$1"; }

command -v gh >/dev/null 2>&1 || die "gh is not installed; cannot check CI." 2
gh auth status >/dev/null 2>&1 || die "gh is not authenticated; cannot check CI." 2

target_sha="$(git rev-parse "${1:-HEAD}")" || die "not a git repository" 2
short_sha="${target_sha:0:8}"

echo "Checking ${WORKFLOW} on ${BRANCH} for ${short_sha}..."

# Ask for the most recent runs on the branch rather than only the latest, because the
# newest run may be for a later commit than the one being tagged.
run_json="$(gh run list --workflow "$WORKFLOW" --branch "$BRANCH" --limit 20 \
  --json databaseId,headSha,status,conclusion,displayTitle 2>/dev/null)" \
  || die "could not reach GitHub to list workflow runs." 2

run_id="$(printf '%s' "$run_json" | jq -r --arg sha "$target_sha" \
  '[.[] | select(.headSha == $sha)] | first | .databaseId // empty')"

if [[ -z "$run_id" ]]; then
  die "no ${WORKFLOW} run found for ${short_sha} on ${BRANCH}.
         A green run on some other commit does not cover the one you are tagging.
         Push the commit and let CI finish before releasing."
fi

status="$(printf '%s' "$run_json" | jq -r --arg sha "$target_sha" \
  '[.[] | select(.headSha == $sha)] | first | .status')"

if [[ "$status" != "completed" ]]; then
  die "the ${WORKFLOW} run for ${short_sha} is still ${status}.
         Wait for it to finish. An unfinished run is not a passing run."
fi

jobs_json="$(gh run view "$run_id" --json jobs 2>/dev/null)" \
  || die "could not read jobs for run ${run_id}." 2

failed=0
for job in "${REQUIRED_JOBS[@]}"; do
  conclusion="$(printf '%s' "$jobs_json" | jq -r --arg n "$job" \
    '.jobs[] | select(.name == $n) | .conclusion' | head -n1)"

  case "$conclusion" in
    success)  ok "$job" ;;
    "")       bad "$job -- not present in this run" "MISSING"; failed=1 ;;
    *)        bad "$job" "$(printf '%s' "$conclusion" | tr '[:lower:]' '[:upper:]')"; failed=1 ;;
  esac
done

# Anything red that is not on the required list still blocks. A job added to the
# workflow but not to REQUIRED_JOBS should not be able to fail silently.
extra_failures="$(printf '%s' "$jobs_json" | jq -r \
  '.jobs[] | select(.conclusion != "success" and .conclusion != "skipped") | .name')"
if [[ -n "$extra_failures" ]]; then
  while IFS= read -r name; do
    printf '%s\n' "${REQUIRED_JOBS[@]}" | grep -Fxq "$name" && continue
    bad "$name -- not on the required list, but red" "FAILED"
    failed=1
  done <<< "$extra_failures"
fi

if (( failed )); then
  die "CI is not green on ${short_sha}. Do not tag.
         Run:  gh run view ${run_id} --log-failed"
fi

printf '\033[32m[clear]\033[0m   all %d jobs green on %s. Safe to tag.\n' \
  "${#REQUIRED_JOBS[@]}" "$short_sha"
