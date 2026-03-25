# BostonUnlockedServer Copilot Instructions

## Build, run, and validation commands

- Before the first build or local run, extract required game resources from a real Shadowrun Chronicles install:
  ```powershell
  .\extractresourcesfrominstallation.ps1 -GameRoot 'C:\Path\To\ShadowrunChronicles'
  ```
  This populates `server\src\Dependencies\`, `server\static-data\`, and `server\StreamingAssets\`, which the host build and runtime expect.

- Primary build command for the local service:
  ```powershell
  Set-Location .\server\src
  msbuild .\Shadowrun.LocalService.sln /t:Build /p:Configuration=Release /p:Platform="Any CPU"
  ```
  The projects are old-style, non-SDK `.csproj` files targeting `.NET Framework 4.8`. `server\start_localserver.ps1` explicitly uses `%WINDIR%\Microsoft.NET\Framework\v3.5\MSBuild.exe`, and `localdocs\copilot_context.md` notes that this legacy toolchain is the expected one for the repo.

- Preferred build-and-run wrapper:
  ```powershell
  .\server\start_localserver.ps1 -UseSqlite
  ```
  Useful switches:
  - `-UseJson`
  - `-MigrateJsonToSqlite`
  - `-SQLiteDbPath <path>`
  - `-Port <httpPort>`
  - `-APlayPort <tcpPort>`
  - `-PhotonPort <tcpPort>`
  - `-NoFileLogs`
  - `-FixedSeed`

- If you touch patch delivery logic, the repo also has a packaging script:
  ```powershell
  .\tools\build_launchie_patch_zip.ps1 -SourceRoot <oldBuild> -TargetRoot <newBuild>
  ```

- There is currently no automated test project, single-test workflow, or lint target in the repository. Do not invent `dotnet test`, `vstest`, or lint commands for this codebase.

## High-level architecture

- This repository is a reverse-engineered local replacement for Shadowrun Chronicles online services. The core runtime is the C# local service under `server\src\`, but it depends on extracted official game DLLs, static data, and streaming assets from the original installation.

- `server\src\Shadowrun.LocalService.Host\Program.cs` is the process entry point. It parses CLI flags into `LocalServiceOptions`, installs custom assembly resolution for extracted game DLLs, creates a `RequestLogger`, and starts three service loops on separate threads:
  - `HttpStubServer` on port `80`
  - `APlayTcpStub` on port `5055`
  - `PhotonProxyTcpStub` on port `4530`

- `server\src\Shadowrun.LocalService.Core\` contains the real implementation. Think of it as several layers:
  - `Http\` handles launcher/config/account/coupon/patch-style HTTP endpoints.
  - `Protocols\` handles the APlay and Photon wire protocols, mission bootstrap, mission commands, hub state, and connection lifecycle.
  - `Persistence\` stores accounts, sessions, friends, player info, and careers. SQLite is the default backend; JSON files still exist as the compatibility/migration path.
  - `Simulation\`, `Metagameplay\`, and `AILogic\` contain the ported gameplay behavior: mission simulation, hub/story progression, rewards/unlocks, and the server-side AI planner.

- `LocalServiceOptions` is the central source of truth for ports, persistence backend, data locations, logs, game-root probing, and portable-resource lookup. The runtime supports both repo-style execution (`server\config`, `server\static-data`) and portable bundle execution (`bin\Release\Resources\...`).

- The Host project post-build step is part of the actual architecture, not just packaging. `Shadowrun.LocalService.Host.csproj` copies `config\`, `static-data\`, extracted `StreamingAssets\`, and `Dependencies\` into `bin\<Configuration>\Resources\` so the built executable can run as a portable localservice bundle.

- `localdocs\` and `decompiled_resources\` are part of the development workflow:
  - `localdocs\api_contracts.md` tracks which HTTP/APlay/Photon contracts are implemented, stubbed, or missing.
  - `localdocs\turn_control_flow.md` documents the client/server turn and mission-command model.
  - `localdocs\copilot_context.md` tracks the current migration state and validated build/runtime assumptions.
  - `decompiled_resources\csharp\` is the main reference when you need the exact client DTO, entity type, field ID, or protocol expectation.

## Key conventions in this codebase

- Large runtime types are intentionally split into partial classes by concern. Follow the existing file layout instead of adding more behavior to a single giant file. Important examples:
  - `APlayTcpStub.*`
  - `PhotonProxyTcpStub.*`
  - `HttpStubServer.*`
  - `LocalUserStore.*`
  - `ServerSimulationSession.*`

- Preserve protocol and DTO shapes exactly. This codebase is driven by reverse-engineered client behavior, not by internal API taste. Before changing HTTP responses, Photon envelopes, entity type IDs, field IDs, or mission-command payloads, cross-check `localdocs\api_contracts.md` and the matching files under `decompiled_resources\csharp\`.

- Prefer the existing structured logging style. `RequestLogger` writes machine-readable JSONL via anonymous objects such as `logger.Log(new { ... })`, `LogLow`, `LogAi`, and `LogPlayerBug`. When adding diagnostics, keep them structured and include stable keys like connection/account context instead of free-form text blobs.

- Reuse the existing registries and identity maps for runtime state. Session, peer, party, hub, and mission state already flow through types such as `ExpiringSessionIdentityMap`, `ConnectedPeerRegistry`, `HubPresenceRegistry`, `CoopGroupHostRegistry`, `PartyHubFollowRegistry`, and `MissionRuntimeRegistry`. Avoid adding parallel ad-hoc global state if one of these abstractions already fits.

- Use `LocalServiceOptions` and the current path-resolution rules instead of hardcoding local paths. The runtime deliberately supports both portable `Resources\...` probing and persistent data outside build output (`data\localservice.sqlite` by default).

- SQLite is the default persistence backend now. JSON mode still matters for legacy compatibility and migration, but new persistence work should normally preserve the SQLite-first path and only keep JSON behavior when existing migration/compatibility code depends on it.

- For mission and AI behavior, extend the ported planners/services rather than reintroducing placeholder logic. The active gameplay migration lives under `AILogic\`, `Simulation\`, and `Metagameplay\`, and `localdocs\copilot_context.md` documents that the older fallback AI path was intentionally removed.
