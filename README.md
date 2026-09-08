# BDVM - Core

`BDVM.Core` provides the module registry, capability discovery, persistence model, diagnostics and authority policies used by the BDVM runtime.

## Status

| Property | Value |
| --- | --- |
| Module kind | Runtime infrastructure |
| Target framework | .NET Framework 4.8 (`net48`) |
| Required module | `BDVM.Common` |
| Standalone install | Not yet |
| Current runtime host | `BDVM.Full` |

The repository already owns its domain and Derail Valley integration sources. During the current migration, `Domain/` and `Integration/` are excluded from `BDVM.Core.csproj` and linked into `BDVM.Full` so there is only one Unity entry point. This is a packaging boundary, not a transfer of ownership.

## Responsibilities

- Track module state and published capabilities with `ModuleStateStore` and `CapabilityRegistry`.
- Define deterministic player and career identity material.
- Encode versioned checkpoint envelopes and preserve unknown module payloads.
- Read and write BDVM state through the Derail Valley `SaveGameData` hook with recovery copies and atomic replacement semantics.
- Export authoritative and visibility-only diagnostics without granting non-host clients mutation rights.
- Centralize host/client authority detection and fail-closed network policy.
- Model a headless dedicated authority and clock policy for future dedicated-server work.
- Enforce manual maintenance policy; no AI driver or autonomous maintenance system is introduced.

## Key surfaces

The main reusable types include `IncrementCheckpointService`, `SaveGameIntegrationCodec`, `SaveGameDataPersistenceService`, `DiagnosticService`, `PlayerIdentity`, `CapabilityRegistry`, `DedicatedClockPolicy` and the reader/writer ports used by the runtime adapters.

## Boundaries

Core does not implement company rules, vehicle ownership, market pricing, contracts or web pages. It provides infrastructure and authority rules to those modules. Dedicated-server support is modeled and testable but is not yet a shipped server executable.

## Dependencies

The project-level dependency is `BDVM.Common`. There is no third-party mod dependency. The integration sources additionally require the proprietary Derail Valley/Unity assemblies and Unity Mod Manager when compiled by `BDVM.Full`; those game files are referenced locally and are not redistributed by this repository.

## Build

Place `BDVM.Common` beside this repository under the same `src/` directory, then run:

```powershell
dotnet build .\BDVM.Core.csproj -c Release
```

To compile the game adapters too, build `BDVM.Full` with a valid `GameDir`.

## Testing and installation

Pure domain behavior is exercised by the BDVM domain validation suite; composition and migration invariants are checked by `tools/Test-W039BdvmMigration.ps1` in the integration workspace. Do not install `BDVM.Core.dll` as a standalone Unity Mod Manager mod yet. Use the matching `BDVM.Full` composition until module packaging is published.

## Compatibility

Checkpoint readers must tolerate unknown payloads and reject corrupt authoritative state safely. Networked mutations are host-authoritative. No compatibility facade or automatic import of legacy `DVCompany` checkpoints is provided.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).
