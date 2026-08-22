# Releasing Foundry

This document describes the process for creating a release of Foundry.

## Single Source of Truth

The project version is centralized in `Directory.Build.props` and automatically inherited by all .csproj files in the solution. This ensures every package, library, and the CLI executable report the same version number.

## Release Process

### Step 0: Confirm Foundry's own CI is green on the commit you are about to tag

**Do this first. Four releases shipped red because it was skipped.**

The failure was not subtle and it was not caught for four versions. Foundry's CI runs
**seven jobs**; a consumer application's CI runs its own, unrelated ones. Watching the
consumer go green says nothing about whether the framework it consumes is broken, and
that is exactly the mistake that was made. Every one of the last four tags was cut on
a commit whose CI run had already failed:

| Tag | Commit | CI on that commit |
| --- | --- | --- |
| `v2.5.0` | `dcddcd7` | failure |
| `v2.6.0` | `60575c4` | failure |
| `v2.7.0` | `973eadf` | failure |
| `v2.8.0` | `d1ecb81` | failure |

The regressions surfaced four releases later as timeouts and empty collections nowhere
near their cause, and were found by bisecting history rather than by any test naming
them. Note that `v2.8.0` — the currently published version — is in that table.

Run the gate:

```bash
scripts/preflight-release.sh          # defaults to HEAD
scripts/preflight-release.sh <sha>    # or an explicit commit
```

It exits `0` only when all seven jobs are green **on that exact commit**, and prints the
`gh run view --log-failed` command for the run when they are not. Do not tag on a
non-zero exit.

The script is the check; the rest of this section is why it exists. If you are checking
by hand instead, the underlying query is:

```bash
gh run list --workflow CI --branch main --limit 1 \
  --json conclusion,status,headSha,displayTitle
```

Two things must both hold:

1. `conclusion` is `success` — not `failure`, and not `null` (still running).
2. `headSha` is the **exact commit you are about to tag**. A green run on an earlier
   commit does not cover the one you are shipping.

All seven jobs must be present and passing. They are separate jobs, and a red one does
not stop the others from being green:

| Job | Guards |
| --- | --- |
| `build-and-test` | The unit suites across every module |
| `outbox-round-trip` | Kafka publication against a real broker |
| `runtime-smoke-test` | Scaffold, run and verify a generated application |
| `distro-binary` | The CLI publishes and reports the right version |
| `schema-gates` | Compiler and IR validation gates |
| `vscode-extension` | The extension builds |
| `studio` | The Studio bundle builds |

If any job is red, **do not tag**. Fix it and re-run. A release is the one moment the
version number becomes permanent for consumers, and it is the worst possible time to
find out the tree was broken.

The job list above is hard-coded in the script rather than read from the run, so a job
quietly disappearing from the workflow blocks the release instead of shortening the
list. Any red job outside the list blocks too — a job can be added to CI without being
added here, and it should not be able to fail silently in the meantime.

The gate was mutation-checked when it was written: it blocks on `d1ecb81` (a commit
from the period when `main` was red, naming *Build and test* and *Outbox round trip*
specifically), on a commit with no run, and on a required job going missing — and it
clears on a green `main`. A gate that has never been observed to fail is not known to
be a gate.

### Step 1: Update Version
Edit `Directory.Build.props` and bump the `<Version>` element:
```xml
<PropertyGroup>
  <Version>X.Y.Z</Version>
</PropertyGroup>
```

Commit this change:
```bash
git add Directory.Build.props
git commit -m "Release X.Y.Z"
```

### Step 2: Create Git Tag
Tag the commit:
```bash
git tag vX.Y.Z
git push --tags
```

### Step 3: CI Produces Versioned Artifacts
When you push a tag, the `distro-binary` job in `.github/workflows/ci.yml`:
1. Captures the tag via `git describe --tags --exact-match` (only when the pushed commit *is* the
   tag; otherwise it falls back to `1.0.0` rather than a bare commit hash, which broke the build
   once already)
2. Passes it to `scripts/build-distro.sh` as the version
3. Publishes the CLI with `-p:Version=vX.Y.Z`, so `foundry version` reports the tagged version
4. Uploads the binary as `foundry-linux-x64-vX.Y.Z` (including the version in the artifact name)

The binary's reported version now matches the git state and the artifact filename, eliminating version ambiguity.

## Notes

- `Directory.Build.props` is the single authoritative source for the project version
- Each .csproj inherits this unless it explicitly sets its own `<Version>` (very rare)
- The CLI reads its version at runtime from the assembly, not from a hardcoded string
- CI automation ensures tagged commits produce versioned artifacts
