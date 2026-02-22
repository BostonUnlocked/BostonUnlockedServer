# Shadowrun BostonUnlockedServer
A fully community-driven Shadowrun Chronicles Boston Lockdown Server Reimplementation.

This repo contains tooling and a C# local service that lets the Shadowrun: Chronicles client talk to a local server.

## Limitations
The server is currently heavily Work-In-Progress (WIP). It has the following limitations (and possibly more unnoticed currently):
1. The AI currently take no actions at all. They immediately end turn. There will be no challenge, but you should be able to kill them.
2. The save progressions will likely have issues. Most of main story progression should work; but there are some bugs around hub transitions / the matrix that cause multiple reloads, and may require game restarts. You may miss some dialog sequences.
3. While generic shop functionality seems to be working now - there's almost certainly some paths where rewards picked up in game or from quest progress or shops, that will just be dropped and lost.
4. When spending nuyen or karma there's likely cases where you are double charged / not charged etc, so any sense of balanced progression won't be there yet.
5. There's a good chance that in future when we get more things working any progress you have now won't be transferrable as we are likely to change the backing storage formats over time.
6. All missions may not be playable yet; and similarly there may be no way to attain your favorite cosmetic / etc yet.
7. Not all authentication paths are re-implemented, only Steam authentication would actually work at this point.

## How you can help
The current non-exhaustive and heavily WIP biggest areas for community contribution are:

1. (Tech) The chat and friends functionality - For once we actually move to a hosted community server. It's probably pretty far off that we actually use this, but this functionality isn't analysed yet.
2. (Content rebuild) Even pretty simple things like what is the starting loadout etc supposed to be - it's more or less a random set of items right now to create characters with. Need details like what the starting sequence of the campaign was, but even fleshing out things like how much starting currency etc.
3. (Tech) Bug tracking is almost certainly going to be a huge thing for the project. We will want to have some community way to report/track bugs. At this point; the set of bugs with each update will change too drastically too, that reporting small bugs is basically not worth it because they're likely to change significantly as we fix bigger issues. But some system setup to collect things that behaved unexpectedly. (And ideally versioned so we can try to focus on bugs that are reported for current version).
4. (Tech) Thinking through auth for non-Steam. There's a lot of account management stuff that isn't implemented. A home rolled auth storage solution is best avoided for now. It's something to get to eventually very likely; but relatively low priority + high risk. Reviewing it for actual security practices would also take a lot of time though.
5. (Tech) Thinking through what is the best distribution method for people to try things. For locally hosted, there's a decent bit of work to get things running locally (dotnet framework 35 + some python packages needed). If we have a remote server setup then just the client patcher should be sufficient to point to that server.
6. (Tech) Setting up some way to repro bugs - the only server side persistence is a couple JSON files, but having a way to say "I encountered X bug" and then have some state that can be loaded to replay the bug would be very valuable. Right now the focus is on the early part of game because characters can be deleted / restart story and test up to the same point pretty quick.

There's likely tonnes more!

## Prerequisites

- Windows + PowerShell
- A local install of **Shadowrun: Chronicles** (Steam)
- **.NET Framework 3.5** (needed for `MSBuild.exe` used by the C# service)
  - Install/enable steps (Microsoft docs): https://learn.microsoft.com/dotnet/framework/install/dotnet-35-windows

## 1) Clone

```powershell
git clone https://github.com/BostonUnlocked/BostonUnlockedServer
cd BostonUnlockedServer
```

## 2) Extract required game resources

The local server needs a few DLLs, static-data JSON, and StreamingAssets copied out of your game install.

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\extractresourcesfrominstallation.ps1 -GameRoot "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles" -InstallPythonDeps
```

This writes into:

- `server/src/Dependencies/` (DLLs)
- `server/static-data/` (JSON)
- `server/StreamingAssets/` (copied folder)

If you move/reinstall the game, re-run the extractor.

## 3) Patch the client to point at your server

The game client reads its endpoint hostnames from:

- `<GameRoot>\Shadowrun_Data\resources.assets`

This repo ships a patch tool executable:

- `clientsetup/patch_embedded_configs.exe`

### Patch for local server (127.0.0.1)

```powershell
.\clientsetup\patch_embedded_configs.exe --asset "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles\Shadowrun_Data\resources.assets" patch --host 127.0.0.1
```

Notes:

- The tool creates a backup next to the asset (by default `resources.assets.bak`).
- `--asset` is a global flag, so it must come **before** `patch`/`restore`.

### Restore to connect to the normal/online servers again

If you patched to `127.0.0.1` and want to revert:

```powershell
.\clientsetup\patch_embedded_configs.exe --asset "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles\Shadowrun_Data\resources.assets" restore
```

Help:

```powershell
.\clientsetup\patch_embedded_configs.exe --help
.\clientsetup\patch_embedded_configs.exe patch --help
```

## 4) Run the local server

Start the server (builds the C# host and launches it):

```powershell
powershell -ExecutionPolicy Bypass -File .\server\start_localserver.ps1
```

Defaults:

- HTTP bind host: `0.0.0.0`
- HTTP port: `80`
- APlay port: `5055`
- Photon port: `4530`

If port 80 fails to bind, run your terminal as Administrator.

## Resetting server progress/state

The server persists state under:

- `server/data/`

To reset all server progress, stop the server and delete that folder:

```powershell
Remove-Item -Recurse -Force .\server\data
```
