# Changelog

All notable changes to DurableLinq are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versioning follows
[Semantic Versioning](https://semver.org/). See [docs/release-process.md](docs/release-process.md) for
how a version number is assigned.

## [Unreleased]

### Added

- Documentation site (MkDocs Material) published to GitHub Pages, covering getting started, concepts,
  architecture, stores, the dashboard, analyzers, and examples.
- CI workflow: build, test (including Testcontainers-backed store suites), and format check on every
  push and pull request.
- Release workflow: git-tag-driven versioning via MinVer, publishing tagged releases to nuget.org and
  every other build to this repository's GitHub Packages feed as a preview package.
- `LICENSE` (MIT), package icon, per-package `README.md` files, and NuGet package metadata
  (`PackageProjectUrl`, `RepositoryUrl`, source link).
- `CONTRIBUTING.md`.

## [0.1.0] - unreleased

Initial engine, hosting integration, Redis/Postgres/SQL Server stores, dashboard, and analyzers. No
tagged release yet — see `CLAUDE.md` and `docs/` for the current shape of the API.
