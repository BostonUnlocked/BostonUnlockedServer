# Hub replication investigation

## Findings

The server was mixing two roster delivery mechanisms without coordinating them.
This explains duplicate remote avatars and provides a client-side explanation for
the associated frozen copies. Cosmetics had a separate missing notification.
Stale character IDs and overlapping connections introduced additional identity
and cleanup inconsistencies.

No affected production logs or multi-client gameplay recordings were supplied.
The findings below distinguish verified code/protocol behavior from the exact
trigger of an individual player's report.

| Finding | Mechanism and consequence |
| --- | --- |
| Full roster followed by incremental replay | Story-hub responses serialized all characters, but did not record them in the receiving peer's announcement set. `TryActivateHubReadiness` subsequently called `ReplayHubRosterToPeer`, sending the same remote characters as additions. |
| Client additions are not idempotent | `HubCharacterManager.AddPlayerCharacter` appends controllers without checking whether that remote character already exists. Full-state initialization and incremental additions both reach this method. |
| Duplicate copies stop moving | The client updater has one action receiver per character ID. Creating the second controller replaces the receiver; the older rendered copy remains, but no longer receives movement. Removing or updating the ID only finds the first matching controller, so duplicates also interfere with later removal/cosmetics. |
| Same-hub full state is not reconciliation | `LocalHubInstanceController.OnHubInstanceReceived` reactivates an existing instance for the same `HubId`; it does not rebuild its remote roster from that response. Repeated full snapshots cannot repair duplicates or reliably refresh cosmetics. |
| Local edits never notify remote hub viewers | The server persisted the slot and replaced the authoritative hub snapshot, but sent only metagame field 33, `CharacterChanged`, to the editing client. Existing remote avatars listen for hub field 8, `PlayerSnapshotChanged`. |
| Untyped character edits use an incomplete fallback | The structured handler required the JSON string `CharacterChangeCollection`, despite APlay field 6 already identifying the request. The other handler covered only a subset of fields and did not send the same acknowledgement. Inventory/outfit-only edits without the marker could be ignored. The inspected stock client includes the marker, so this compatibility gap is not the explanation for its missing remote cosmetic updates. |
| Movement/entry can change presence identity | Movement accepted a different same-account character ID and rekeyed presence; entry trusted the supplied ID outright. Stale career IDs could disagree with the canonical local character and leave stale authoritative entries. |
| Character claims and cleanup race | Duplicate retirement and presence registration were separate operations. The registry could retain multiple participants for one character while its character index pointed to only one. Late cleanup could remove state belonging to a replacement connection. |
| Announcement state can get ahead of delivery | Add/replay recorded announcements before sending, and the send method swallowed socket failures. Movement could overtake an addition, or the server could permanently believe a never-delivered avatar had been announced. |
| Roster reads escape the instance lock | `PortedHubInstance.HubState` exposed the mutable dictionary while other connections added/removed characters. Concurrent enumeration could fail or yield inconsistent replication work. |

### Why a player can see a copy of their own character

The client deliberately excludes the local **character identifier**, not the
account or display name, from remote avatar creation. Correctly identified local
entries alone do not explain self-duplicates. However, the old entry/movement
paths could register a stale/different identifier while continuing to use the
active career's appearance. That entry is no longer excluded as the local
character. Overlapping sessions and stale authoritative entries make the
mismatch persist across roster snapshots.

This is a verified acceptance path, not proof that every reported self-duplicate
used that particular sequence. Identical names or appearances do not establish
that two avatars have the same identifier.

## Client contracts checked

The installed game client's `SRO.Client.dll` and
`ShadowRunOnline_Client.dll`, plus the extracted common serializers, were
inspected locally. No client binaries or decompiled source are included in this
change.

| Entity / field | Purpose |
| --- | --- |
| Metagame / 6 | `ChangeCharacter` request |
| Metagame / 33 | `CharacterChanged`, updating the local player controller |
| Metagame / 37 | `SendHubCommunicationObjectToClient`, hub instance handoff |
| Hub / 1, 2, 3 | Move, enter, leave requests |
| Hub / 4 | `ExecuteMoveToPosition`, remote movement |
| Hub / 7 | `HubStateChanged`, incremental membership |
| Hub / 8 | `PlayerSnapshotChanged`, remote appearance/snapshot update |

`HubCharacterManager`, `HubClientServerCommunicationUpdater`,
`HubCommunicationObject`, and `MetaGameplayCommunicationObject` establish these
separate local/remote update paths. The client queues hub communication until the
scene's receivers are registered. Remote snapshot updates invoke
`LogicCharacterController.Update`, which raises visualization changes and
recreates the visual model when the appearance hash changes.
The stock JsonFx writer emits a `TypeName` hint for `CharacterChangeCollection`;
accepting field-6 payloads without it also preserves the declared DTO contract.

## Fix

Use **one source of remote roster membership**: an empty full-state handoff,
followed by incremental additions after readiness. The handoff retains its real
hub identity/name; the local character is still initialized through the client's
separate local-player path. Same-hub refreshes preserve delivery bookkeeping.
Changing instances resets the receiving peer's lifecycle state. All hub-handoff
send sites, including rejected-mission restoration, use the same helper.

Serialize additions, removals, movement, and snapshot notifications on each
recipient's stream. Commit an announcement only after its add frame is written
successfully, and recheck source ownership/recipient hub after acquiring that
stream so stale fanout cannot leak across an instance change. Writes have a
two-second timeout; a failed write closes that transport because a partially
written frame cannot safely be retried on the same connection.
Sending a cosmetic update must never clear announcement tokens or
remove/re-add an avatar. A peer not yet announced receives the current
authoritative snapshot in its eventual addition.

Use the structured character-change handler for canonical field-6 requests,
whether or not JSON includes a type marker, and remove the divergent duplicate
parser. Persist first, refresh authoritative state, then publish field 8 to
existing remote viewers. Keep field 33 for the editing client.

Hub entry/movement must match the server-registered active account, career, and
exact character identifier before updating position. A movement packet is not a
career-switch instruction. Character ownership must be unique in the registry,
and retiring a connection must not let its subsequent cleanup delete its
replacement's authoritative character. Hub cleanup also runs in the connection's
`finally`, not only on a normal read-loop exit.
Authoritative transition plus presence registration share the lifecycle lock
with removal, and a retired stream cannot register again.

Return detached hub-state views under the instance lock so roster readers never
enumerate the mutable authoritative dictionary.

## Verification and limits

The focused executable in `tools\HubReplicationRegression.cs` drives the real
APlay handler through loopback TCP connections rather than requiring Unity.
Run after extracting game resources and building the Release solution:

```powershell
.\tools\test_hub_replication.ps1
.\tools\test_social_presence.ps1
```

The hub regression covers wire-level handoff/add/movement/snapshot behavior,
same-hub refresh, rejected stale-career IDs, persistence and acknowledgements of
untyped edits, stock-style typed inventory-only updates, failed add delivery,
detached roster views, concurrent registry claims, replacement-session cleanup,
and one canonical removal when the final owner disconnects.
It runs with JSON and SQLite storage. The existing social regression protects
the selected-career profile/invalidation behavior shared with character changes.

Before implementation, the untyped customization probe timed out waiting for
field 33, and the concurrent-claims probe retained multiple character owners.
The legacy MSBuild 3.5 solution invocation also failed on the Host project's
`ToolsVersion="Current"`; the installed Visual Studio MSBuild successfully builds
the .NET Framework 4.8 solution. This toolchain issue is not a replication fix.

These are server/protocol regressions, not a claim that Unity rendering or the
specific production timing has been reproduced end to end. Final gameplay
confirmation should use multiple stock clients: staggered joins, repeated hub
requests, cosmetics while stationary/moving, career changes, mission return,
disconnect/reconnect, and overlapping logins. Check that each remote character
has one visual model and that movement and cosmetics update that same model.

The existing hub-ready timer is diagnostic, not a recovery mechanism: it reports
whether readiness occurred. This change does not invent readiness before the
client loads a hub. Account-to-hub routing with simultaneous different careers
on one account remains a separate policy question; it should not be resolved by
allowing movement to select an arbitrary character.
