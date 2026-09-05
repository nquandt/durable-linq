# Release process

Package versions come from git tags. No project file has a hand-edited `<Version>`; the version is
computed at build time by [MinVer](https://github.com/adamralph/minver), configured in
`Directory.Build.props`.

## How a version is computed

- MinVer looks for the nearest git tag matching `v*.*.*` (the `v` prefix is configured with
  `MinVerTagPrefix`).
- **On that exact commit:** the package version is the tag with the `v` stripped. Tag `v1.2.3` produces
  package version `1.2.3`.
- **On any other commit** (a branch, or `main` between tags): the version is the next patch above the
  last tag, with a preview suffix and the commit height and hash, for example
  `1.2.4-preview.0.3+af12c9d`. Before any tag exists at all, this starts from `0.1.0-preview.0...` (set by
  `MinVerMinimumMajorMinor`).

This means every build — including a plain `dotnet pack` on your machine with no tag nearby — produces a
valid, ordered, installable package version, and only an intentional tag push produces a "real" release
version.

## What CI does with that version

`.github/workflows/release.yml` runs on every push to `main` and on every tag matching `v*.*.*`:

1. **`test`** — runs the same build-and-test job as `ci.yml` (build, test, format check). Nothing packs
   or publishes if this fails.
2. **`pack`** — runs `dotnet pack` across the solution and uploads the resulting `.nupkg` files as a
   build artifact. This step alone doesn't know or care whether it's a tag; MinVer already decided the
   version.
3. **Publish, and only one of the following runs:**
     - **Tag push** (`refs/tags/v*.*.*`): `publish-release` pushes every package to **nuget.org**, using
       the `NUGET_API_KEY` secret. This is the only path that reaches nuget.org.
     - **Anything else** (a merge to `main`): `publish-preview` pushes every package to this
       **repository's GitHub Packages feed** instead, using the built-in `GITHUB_TOKEN`. Nothing you
       merge to `main` reaches nuget.org until you also tag it.

## Cutting a release

1. Merge everything you want in the release to `main` and confirm `ci.yml` is green.
2. Move the relevant entries in `CHANGELOG.md` from "Unreleased" to a new version heading.
3. Tag the commit and push the tag:

   ```
   git tag v1.2.3
   git push origin v1.2.3
   ```

4. `release.yml` runs on the tag push and publishes that exact version to nuget.org.

## Consuming a preview package

Preview packages (anything with a `-preview.` suffix) are on this repository's GitHub Packages feed, not
on nuget.org. Add the feed as a package source to try one before a tagged release exists:

```
dotnet nuget add source --username <your-github-username> --password <a GitHub PAT with read:packages> \
  --store-password-in-clear-text --name durable-linq-preview \
  "https://nuget.pkg.github.com/nquandt/index.json"
```

## Required repository secrets

- `NUGET_API_KEY` — an API key from nuget.org scoped to this project's packages. Only needed for the
  `publish-release` job; the preview path uses the automatically provided `GITHUB_TOKEN`.
