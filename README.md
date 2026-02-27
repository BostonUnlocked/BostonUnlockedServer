# Shadowrun BostonUnlockedServer

A fully community-driven Shadowrun Chronicles: Boston Lockdown Server Reimplementation.

This free server has been reverse engineered from the game client using a clean-room reverse engineering approach. The code provided does not contain any proprietary code or assets and requires official game files to work.

This repository contains tooling and a C# local service that lets the Shadowrun Chronicles: Boston Lockdown client talk to a local server (BostonUnlocked). In short, you can run your own server to play the game and invite friends to play with you.

**Community Discord Sever Link:** <https://discord.gg/cyuPZrC8Wr>

## This server is currently heavily Work-In-Progress (WIP). There are therefore bugs and saves may not work from one update to another. Help us revive the game: Please report bugs by creating an Issue in the Issues tab on this repository. For help, reach out in the Community Discord in the support channel

## Features

This non-exhaustive list covers the main features of the server currently:

1. Login to the game (Steam only for now)
2. Host your own server:
    1. Play offline solo, see [Installation of the full server for local and offline play](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#installation-of-the-full-server-for-local-and-offline-play)
    2. Host multiplayer servers - **with important caveats, see [How to host your own multiplayer server](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#how-to-host-your-own-multiplayer-server)**
    3. Connect to someone else's server, see [How to connect to someone else's server](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#how-to-connect-to-someone-elses-server)
3. Chat, friends, groups - hubs currently don't sync players visibly together but group features such as rewards, chat, playing in quests together with both visible, etc work
4. Working NPC AI, shops, quests, etc
5. DLC support via coupons, see [Enabling DLC](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#enabling-dlc) - **this includes all DLC, even previously restricted backer rewards so everyone can experience all the content officially released**

**All features should be considered WIP and there might be bugs. Please report bugs by creating an Issue in the Issues tab on this repository. For help, reach out in the Community Discord in the support channel.**

## Limitations

As the server is currently heavily Work-In-Progress (WIP). It has the following limitations (and possibly more unnoticed currently):

1. The save progressions will likely have issues. Most of main story progression should work; but there are some bugs around hub transitions / the matrix that cause multiple reloads, and may require game restarts. You may miss some dialog sequences.
2. While generic shop functionality seems to be working now - there's almost certainly some paths where rewards picked up in game or from quest progress or shops, that will just be dropped and lost.
3. When spending nuyen or karma there's likely cases where you are double charged / not charged etc, so balanced progression may be affected.
4. There's a good chance that in future when we get more things working any progress you have now won't be transferrable as we are likely to change the backing storage formats over time.
5. All missions may not be playable yet; and similarly there may be no way to attain your favorite cosmetic / etc yet.
6. Not all authentication paths are re-implemented, only Steam authentication works at this point.
7. The AI was server-side so this re-creation may not match the original fully.

## How you can help

The current non-exhaustive and heavily WIP biggest areas for community contribution are:

1. (Content check) Verifying game behaviour of the community server vs the vanilla game. Even simpled details like what is the starting loadout supposed to be, does the correct dialogue show up in sequence. We need details like what the starting sequence of the campaign was, but even things like how much starting currency, etc. To assist with this here are some resources:
    * [All NPC and Mission Dialogue](https://steamcommunity.com/sharedfiles/filedetails/?id=859974620)
    * [Video of a complete playthrough of the game](https://www.youtube.com/playlist?list=PLJeCfXtw78_9YzNM4QrABDO0jiI0gWQ8Q)

    **Please report bugs by creating an Issue in the Issues tab on this repository. For help, reach out in the Community Discord in the support channel.**

2. (Tech) Building auth for non-Steam. There's a lot of account management stuff that isn't implemented. A home rolled auth storage solution is best avoided for now. It's something to get to eventually very likely but relatively low priority + high risk. Reviewing it for actual security practices would also take a lot of time.
3. (Tech) Setting up some way to repro bugs - the only server side persistence is a couple JSON files, but having a way to say "I encountered X bug" and then have some state that can be loaded to replay the bug would be very valuable. Right now the focus is on the early part of game because characters can be deleted / restart story and test up to the same point pretty quick.

There's likely tonnes more!

## Installation of the full server for local and offline play

### 0) Install prerequisites

* Your system must be running Windows and have PowerShell enabled (that should be on by default for standard installations)
* Install Git from <https://git-scm.com/install/windows> (add it to PATH when it asks you to)
* Have a local install of **Shadowrun Chronicles: Boston Lockdown** (Steam version)
    >**Note:** In this guide, the game's installation path is assumed to be "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles". If you installed the game elsewhere, replace the path in the commands where this path is used with where you installed the game
* Install **.NET Framework 3.5** (needed for `MSBuild.exe` used by the C# service)
  * The install/enable steps are in this Microsoft Docs document: <https://learn.microsoft.com/dotnet/framework/install/dotnet-35-windows>

### 1) Clone this repository

1. Open a PowerShell window in the folder where you wish to install the server - this can be done by SHIFT+Right clicking in the folder from Windows Explorer and choosing "Open PowerShell Window here" or from the Windows Explorer menu "File->Open Windows PowerShell" within the folder.
2. From PowerShell run the commands below

```powershell
git clone https://github.com/BostonUnlocked/BostonUnlockedServer
cd BostonUnlockedServer
```

### 2) Extract required game resources

The local server needs a few DLLs, static-data JSON, and StreamingAssets copied out of your game install.

Run:

```powershell
powershell -ExecutionPolicy Bypass -File .\extractresourcesfrominstallation.ps1 -GameRoot "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles" -InstallPythonDeps
```

This writes into:

* `server/src/Dependencies/` (DLLs)
* `server/static-data/` (JSON)
* `server/StreamingAssets/` (copied folder)

If you move/reinstall the game, re-run the extractor.

### 3) Patch the client to point at your server

The game client reads its endpoint hostnames from:

* `<GameRoot>\Shadowrun_Data\resources.assets`

This repo ships a patch tool executable:

* `clientsetup/patch_embedded_configs.exe`

#### Patch for local server (127.0.0.1)

```powershell
.\clientsetup\patch_embedded_configs.exe --asset "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles\Shadowrun_Data\resources.assets" patch --host 127.0.0.1
```

Notes:

* 127.0.0.1 is the IP for localhost. Use this to play via your local server specifically. If you are connecting to a multiplayer server, this should be the public IP or hostname of the server as mentioned in [How to host your own multiplayer server](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#how-to-host-your-own-multiplayer-server) and [How to connect to someone else's server](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#how-to-connect-to-someone-elses-server)
* The tool creates a backup next to the asset (by default `resources.assets.bak`).
* `--asset` is a global flag, so it must come **before** `patch`/`restore`.

#### Restore to connect to the normal/online servers again

If you patched to `127.0.0.1` and want to revert:

```powershell
.\clientsetup\patch_embedded_configs.exe --asset "C:\Program Files (x86)\Steam\steamapps\common\ShadowrunChronicles\Shadowrun_Data\resources.assets" restore
```

Notes:

* `--asset` is a global flag, so it must come **before** `patch`/`restore`.

Help:

```powershell
.\clientsetup\patch_embedded_configs.exe --help
.\clientsetup\patch_embedded_configs.exe patch --help
```

### 4) Run the local server

Start the server (builds the C# host and launches it):

```powershell
powershell -ExecutionPolicy Bypass -File .\server\start_localserver.ps1
```

Defaults:

* HTTP bind host: `0.0.0.0`
* HTTP port: `80`
* APlay port: `5055`
* Photon port: `4530`

If port 80 fails to bind, run your PowerShell terminal as Administrator.

### 5) Run the game and connect to the server

Start the game normally, connect to the server as you would normally and play!

## How to host your own multiplayer server

**We do not recommend hosting a multiplayer server from your personal computer (see Note 3 below for why) due to potential security issues**

1. Install the server as described above
2. Take the local server you've built/and run with the start local server script, then copy the full folder from:

* \server\src\Shadowrun.LocalService.Host\bin\Release

To the machine you want to host it on.
3. Update these files

* Release\Resources\config\config.xml
* Release\Resources\config\LauncherConfig.xml

To replace all instances of 127.0.0.1 with the reachable public ip / hostname of your server.

**Notes:**

1. If you're hosting on a LAN for local play, this can be a local IP 192.168.x.x
2. If you want to play with other people over the Internet, it needs to be the public IP or a hostname that resolves to the public IP of your server.
3. If you are hosting your own multiplayer server, you will need to port forward all 3 of the ports mentioned in step 4 of the installation instructions. This can cause security risks as one of them is port 80 so **hosting a multiplayer server is best done if you know how to host servers already and how to secure your system and networks**.

## How to connect to someone else's server

**Make sure you trust whom you are connecting to!**

1. Download the patching program from:
<https://raw.githubusercontent.com/BostonUnlocked/BostonUnlockedServer/refs/heads/main/clientsetup/patch_embedded_configs.exe>

2. Run it by double clicking on it
3. It will open a file browser asking you to point to the resources.assets file in the Steam installation folder for the game. Select the file and click on "Open" to proceeed.
4. After this, in the command line it will ask for a host, simply enter the IP of the server you want to connect to and press ENTER.
5. Once done, simply launch the game normally and enjoy!

## Enabling DLC

1. From the game's launcher, click on Coupons
2. Add the coupon codes in this document to enable the DLC you want:
[Coupon codes list](https://github.com/BostonUnlocked/BostonUnlockedServer/blob/main/docs/coupon_codes.md)

## Server progress/state (aka saves)

### Server Data Folder

The server persists state under the server data directory which can be either:

* If the data directory already exists in the server's installation directory:

    >`server/data/`

* If the data directory does not exist in the server's installation directory, it will be saved in:

    >`%LOCALAPPDATA%/ShadowrunLocalService`

If you connect to someone else's server, that server is where your saves will be stored.

### Backing up server progress/state

To backup all server progress, stop the server and copy the server data folder to where you want to back it up

### Restoring server progress/state

To restore server progress, stop the server and copy the server data folder from your backup into the server's corresponding data directory. After starting up the server again, you will regain the progress stored.

### Resetting server progress/state

To reset all server progress, stop the server and delete the server data folder:

```powershell
Remove-Item -Recurse -Force <Server Data Folder>
```

Where <Server Data Folder> is the appropriate location mentioned in [Server Data Folder](https://github.com/BostonUnlocked/BostonUnlockedServer?tab=readme-ov-file#server-data-folder)
