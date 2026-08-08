# Releasing Foundry

This document describes the process for creating a release of Foundry.

## Single Source of Truth

The project version is centralized in `Directory.Build.props` and automatically inherited by all .csproj files in the solution. This ensures every package, library, and the CLI executable report the same version number.

## Release Process

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
1. Captures the tag via `git describe --tags --always`
2. Passes it to `scripts/build-distro.sh` as the version
3. Publishes the CLI with `-p:Version=vX.Y.Z`, so `foundry version` reports the tagged version
4. Uploads the binary as `foundry-linux-x64-vX.Y.Z` (including the version in the artifact name)

The binary's reported version now matches the git state and the artifact filename, eliminating version ambiguity.

## Notes

- `Directory.Build.props` is the single authoritative source for the project version
- Each .csproj inherits this unless it explicitly sets its own `<Version>` (very rare)
- The CLI reads its version at runtime from the assembly, not from a hardcoded string
- CI automation ensures tagged commits produce versioned artifacts
