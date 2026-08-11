# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.10.x] - 2026-08-11

### Added
- **Strict Agent Skills Mode (`Mode = "agent-skill"`)**:
  - Added strict validation adhering to the official Agent Skills specification (`SKILL.md`).
  - Allowed top-level fields: `name`, `description`, `license`, `compatibility`, `metadata`, `allowed-tools`.
  - Enforced lowercase alphanumeric kebab-case naming (`^[a-z0-9]+(-[a-z0-9]+)*$`, 1–64 chars) matching the parent directory name.
  - Enforced length constraints for `description` (1–1024 chars) and `compatibility` (1–500 chars).
  - Enforced string-only scalar map entries in outer `metadata`.
- **Embedded Metadata Projection (`EmbeddedMetadataKey`)**:
  - Optional static parameter on `FrontMatterProvider` to extract and parse embedded YAML string blocks under `metadata` (e.g. `dev.v-san.skills`).
  - Automatic cross-file schema inference for embedded data structures.
  - Strongly-typed projection via `ExtensionMetadata : ExtensionMetadataData option` with IDE autocomplete, preserving the outer string `Metadata` view.
- **Enhanced Validation Diagnostics**:
  - Added `ValidationFailure.UnknownField`, `ValidationFailure.InvalidFormat`, and `ValidationFailure.InvalidEmbeddedMetadata` DU cases.
- **CLI Enhancements (`dotnet-yamlfm`)**:
  - Added `--mode agent-skill|skill|general` and `--embedded-key <key>` flags.
  - Formatted failure diagnostics for CI and automated verification with exit code 2 on validation errors.
- **Documentation**:
  - Added detailed comparison table of validation modes (`"agent-skill"`, `"skill"`, `"general"`) to [README.md](README.md).

### Fixed
- **FSharp.Core Dependency Versioning**:
  - Pinned `FSharp.Core` package reference version to `10.*` in `Directory.Build.props` to prevent `NU1605` package downgrade warnings on .NET 10.0.10x installations and `dotnet fsi`.
- **Validation-Aware Schema Discovery**:
  - `Describe()`, `--schema`, and `FrontMatterDefinition` now filter out documents failing schema validation so rejected samples (e.g. unexpected fields or malformed embedded YAML) do not pollute the generated schema.
