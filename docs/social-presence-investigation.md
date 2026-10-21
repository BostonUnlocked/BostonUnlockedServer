# Social presence and one-way chat investigation

Related player report: [#82](https://github.com/BostonUnlocked/BostonUnlockedServer/issues/82).

## Findings and confidence

There are confirmed Photon transport defects, a separate selected-career profile
consistency defect, and a client-side failure mechanism that can explain invisible
chat together with stale friend status. The transport and stale-career profile
fixes in this change are reproducible, but **the exact production
trigger of the combined symptoms has not been captured**. This is not a claim
that every instance of #82 is resolved.

| Finding | Evidence | Treatment |
| --- | --- | --- |
| A quiet authenticated Photon connection is closed after two seconds | Reproduced against unchanged HEAD with real loopback sockets | Fixed: treat a read timeout as shutdown polling, not disconnection |
| Early returns bypass Photon peer and transport cleanup | Controlled first-response write failure left the account online even after its handler exited on unchanged HEAD | Fixed: cleanup runs in `finally`; the same probe leaves the account offline |
| Events and responses can write to the same stream concurrently | Events used a peer-local lock, but replies and pings bypassed it | Fixed: serialize every complete write on the shared stream |
| A failed/omitted sender-profile result can permanently withhold a chat callback | Verified installed client resolver and chat display paths | Client retry/failure-handling recommendation below; no client binaries changed |
| Career selection can leave the previous career's chat snapshot persisted | Before/after regression using the installed client's snapshot codec | Fixed: project the selected career into both profile names and `PlayerCharacter` |
| A save of a nonselected career can overwrite profile names | Career saves previously synchronized names regardless of the selected index | Fixed: only the selected career updates the account profile |
| Persisted profile changes lack server-originated info-channel cache invalidation | The client can announce its own update before the HTTP write completes | Fixed for stored profile changes: persist first, then publish the changed account on `SRO_Default_Info`; mission-only transitions remain separate |

No production server request logs were supplied. The public issue's pasted client
logs show normal hub/group joins, but no actionable profile-request exception.

## What the client actually considers online

There are three distinct mechanisms; fixing only a Boolean in the friend response
does not cover all of them.

1. Photon `User.IsOnline` is populated by friend-list responses and
   `FriendChangedMessage`.
2. HTTP `GetPlayerInfo` supplies `Online`, `InMission`, and `PlayerCharacter`.
   The server computes the status flags from `AccountTransportLivenessRegistry`
   and `MissionRuntimeRegistry`, rather than trusting persisted client flags.
3. `PlayerInfoChangeNotificationManager` joins **`SRO_Default_Info`**. Its
   participant roster is the client's online set. When a user disappears from
   that roster, cached info is marked offline and not in mission. The manager
   overlays roster membership onto resolved profiles, except while a refresh
   for that account is open.

`FriendsController.UpdateFriendsOnlineCount` additionally requires resolved
`SROPlayerInfo`, `IsOnline`, and `HasBasicSROInfo`. An account can therefore have a
live game transport without a usable online friend entry on another client.

The server's `ChatAndFriendsState` tracks channel membership by connection and
deduplicates the roster by account. One connection leaving must not remove the
account while another joined connection remains. APlay liveness and membership
in the Photon info channel are not interchangeable.

## Why the sender can see their own text while others do not

The installed client's `ChatChannel.ProcessInput` submits `SendMessage`, ignores
the returned promise, and immediately inserts a local echo. Seeing that line is
not a delivery acknowledgement.

On another client, `ChatChannel.OnNewMessageReceived` requests the sender's
`GetMostRecentPlayerInfo` and inserts the line only inside its callback. This
method does not perform an unconditional fresh HTTP request: cached info returns
immediately; an uncached ID goes through `PlayerInfoResolver`.

The resolver marks each pending request `RequestSent = true` before querying the
account service. Successful per-player results populate the cache, and callbacks
run only for IDs now present in that cache. If a per-ID result is an error or is
omitted, its sent request can remain unresolved without being selected for
retry. Later callbacks for that same ID attach to the stuck request. A failed
refresh can also leave that account in `mOpenPlayerInfoRequests`, preventing
subsequent online-state overlays.

That is a concrete mechanism for a recipient to display neither the sender's
new chat rows nor fresh friend status, while the sender can still receive other
players' messages. The failure is per account and per receiving client, which
also permits inconsistent observations between players. Restarting the receiving
client resets its in-memory cache/request state.

A missing character name alone is **not** enough: the chat renderer can show
message text without a username. Likewise, missing profile-change notifications
alone do not suppress chat for an already cached sender. Neither should be
presented as the proven cause of the entire symptom pair.

On the server, `BroadcastTextMessage` routes by recipient channel membership and
excludes the sender's account to avoid duplicate local echoes. It does not reject
a message because a recipient's friend list labels its sender offline.

## Confirmed server transport fixes

`PhotonProxyTcpStub.HandleClient` set `ReceiveTimeout = 2000`. `ReadChunk` caught
every read exception and returned the same empty array used for EOF. The outer
loop then closed and unregistered an otherwise healthy connection. The two-second
value now remains a polling bound for service shutdown; only EOF, a real receive
failure, or shutdown ends that read loop. Real receive failures are logged with
the peer endpoint.

Peer/liveness cleanup previously followed the request loop rather than being
inside its `finally`. Returning after a failed init, ping, or operation response,
or a requested strict disconnect, skipped it. That could retain dead account
connections and channel entries and distort subsequent online transitions.
Cleanup now runs for every exit, including early returns.

Finally, `ChatAndFriendsState.SendEventToPeer` previously synchronized only event
writes with other event writes. Operation replies and ping responses could still
write concurrently. `SendRaw` now serializes the complete write on the
`NetworkStream` shared by all three paths. This removes an unsupported concurrent
writer arrangement; it is not evidence that a particular production packet was
corrupted.

The player's approximate twenty-second observation is not treated as proof of a
twenty-second Photon keepalive interval. That interval was not established from
the inspected client code.

## Contracts checked and ruled out

The installed client accepts participant change byte **1 as added**, and **0 as
deleted**. Its channel-name comparison is exact. The existing server encoding
and `SRO_Default_Info` spelling are correct.

The account-service response is `PlayerInfoResults`, with each result containing
`IdentityHash`, `Code`, `Message`, and `PlayerInfo` key/value pairs. Both outer
and per-result success codes are **0**. The current server supplies that shape;
regressions check that every requested sender identity is returned.

`SROPlayerInfo.GetBool` uses `Boolean.Parse`, so the server's `"True"` and
`"False"` values are valid. Changing their casing would not fix this issue.
Character names come from `PlayerCharacterSnapshot.CharacterName`; they are not
fields of Photon `User` or `TextMessageEventParameters`.

## Previous-career names are a separate defect

The Photon lifecycle fixes alone do not fix a wrong career name. Chat uses the
`PlayerCharacter` snapshot, not the separate `CharacterName` or `DisplayName`
profile strings. The installed client encodes it with
`JsonFxSerializerProvider.Current.Serialize<IPlayerCharacterSnapshot>`, then
`ZipUtilities.Compress`: JsonFx JSON with the stock member map/type hints,
UTF-16LE text, Deflate compression, and Base64. APlay serialization or ordinary
uncompressed JSON is not interchangeable with that format.

Before this fix, the server had several consistency gaps:

1. `UpsertCareer` updated the string names but never replaced `PlayerCharacter`.
   Saving career B and selecting it could therefore produce B's string fields
   alongside career A's chat name and snapshot.
2. `SetLastCareerIndex` only persisted the index. It did not synchronize any
   profile fields. A later save of a different, nonselected career could also
   replace the account's string names.
3. HTTP `SetPlayerInfo` could normalize a mismatching display-name suffix and
   **remove the incoming snapshot from the update**, leaving the previously
   persisted snapshot untouched. Snapshot-only updates bypassed that suffix
   check entirely.
4. The stock client uploads its new snapshot after APlay `EnterCareer` succeeds,
   the selected career snapshot is applied, and chat connects. However, it does
   **not await the HTTP upload before scheduling its info-channel notification**.
   A peer can reload too early, cache the old profile, and receive no later
   server notification when the write finally completes.

The server now makes the persisted selected career authoritative for the profile
name strings and the complete chat snapshot. It reuses the existing APlay
snapshot builder, including appearance, equipment, skills, and wallet values,
and the official extracted JSON/compression helpers. Selection and saves of the
selected career persist the corrected profile; a late save of a different career
does not replace it. Client uploads cannot replace those fields with an older
career, even when only `PlayerCharacter` is supplied. Profile reads and returned
search rows also project the selected career, so historical stale data is not
served while waiting for another upload. This read projection does not itself
rewrite the stored row; selection or a subsequent profile/career write does.

Actual persisted profile changes publish an ordinary empty text event on
`SRO_Default_Info`, with the changed account GUID as sender, after the store
update and outside the local update lock. Existing interested peers can then
reload the corrected profile. This closes the upload/announce ordering gap
without relying on the stock client's fire-and-forget announcement. Coupon
entitlement reads use raw stored keys to avoid reentering career projection.
The blob-format diagnostic now checks compressed JSON, rather than incorrectly
trying the APlay message decoder.

### What the server-restart observation establishes

An affected sender restarting their own client does not clear other clients'
cached profiles. Persisted stale snapshots can also be returned again to a
restarted receiving client. These are stronger explanations than a transient
username rendering problem.

However, **no server-lifetime player-profile cache was found**: JSON and SQLite
profile values are read from storage. Restarting the server does not erase a
persisted old blob. It does force connection/session reconstruction and can
change subsequent publication/reload ordering, but that explanation of the
reported recovery has not been reproduced with two game clients. If restarting
all receiving clients still fails while only a server restart reliably repairs
the name, additional runtime evidence is needed; this change does not claim that
exact restart-only trigger is proven. The fix removes the reproduced stale
snapshot and publication-order dependencies rather than assuming a nonexistent
profile cache.

## Remaining proposed fixes and production confirmation

The durable fix for the client resolver latch is to complete or reset per-ID
requests on every failure/omission, clear open-refresh bookkeeping on both
success and failure, and allow bounded retry. A failed profile lookup should not
hold a received chat line indefinitely; a cached or anonymous-sender fallback can
display its text while metadata is retried. This repository change does not
patch or redistribute the proprietary client.

Server-side cache invalidation now covers persisted profile changes from career
selection, selected-career saves, and HTTP updates. Authoritative mission-only
transitions still need independent notification coverage; their HTTP status
projection remains unchanged. Neither profile invalidations nor corrected
snapshots retry an already stuck sent profile request, so these changes are not
substituted for the resolver fix.

For a production reproduction, correlate an affected sender GUID and receiver
with the last inbound Photon frame, `photon-error`, `transport-disconnected`,
`http-playerinfo` requested/returned identities, and `http-error` records. Check
the actual account-service response for a per-player failure, omitted ID, timeout,
or client-side profile conversion failure. Also compare that receiver's
info-channel roster with the server roster. Do not collect authentication tickets
or credentials. These observations distinguish socket loss, stale channel
membership, and a blocked metadata callback instead of assuming they are one
condition.

## Reproduction and regression checks

Extract the official dependencies and build the Release solution first. The
current Host project declares `ToolsVersion="Current"`; use an installed MSBuild
that understands it. The legacy 3.5 executable on this machine built Core but
could not build Host; the installed Visual Studio MSBuild built the full solution.

Run:

```powershell
.\tools\test_social_presence.ps1
```

The script compiles a standalone .NET Framework harness, uses an ephemeral
loopback Photon port and isolated temporary data, and removes its temporary
output afterwards. It does not introduce a test project or a package dependency.
It runs separately against JSON and SQLite.

Coverage includes authenticated sender identity, exact info-channel roster and
add/remove event values, overlapping reconnects, bidirectional remote chat,
HTTP profile response identities/result codes, live mission flags, concurrent
large events and operation replies, idle connections surviving 2.5 seconds,
final-socket cleanup, and listener shutdown. The idle assertion fails on an
unchanged HEAD build and passes with this change.

The harness additionally switches from career A to B using the server's
save-then-select ordering, decodes the resulting profile with the actual client
codec, and checks the selected name/identifier, portrait, wallet, and equipment
metadata. It checks an info-channel invalidation with the correct sender,
delayed saves of A, mismatched and snapshot-only stale HTTP uploads, deliberately
injected historical stale rows, a recreated store/HTTP service, reselecting the
same index, a Unicode rename, search results, and preservation of non-SRO data.
Both JSON and SQLite pass. Against transport-only commit `e187796`, the new
regression fails because B's profile still decodes to A's chat name. This
isolates the career-profile defect from the already-fixed socket defects.

These checks exercise real server sockets and official extracted Photon
serialization DTOs, not the Unity UI or the client's resolver failure callbacks.
A separate controlled fault-injection probe closed the server stream after
account registration but before its first authentication response, then waited
for the handler's connection context to be removed. Unchanged HEAD still
reported that account online; the fixed build reported it offline. This verifies
the early-return cleanup path independently of ordinary socket EOF.

A two-game-client production reproduction remains necessary before declaring
the combined player report fully resolved.

## Source map

Server:

- `Protocols/PhotonProxyTcpStub.cs`: connection loop, receive timeout, cleanup,
  friend notifications, shared writes.
- `Protocols/PhotonProxyTcpStub.Operations.cs`: account binding, channel/friend
  requests, sender identity.
- `Protocols/PhotonProxyTcpStub.ChatAndFriendsState.cs`: roster, fanout,
  reconnect deduplication, pushed events.
- `Http/HttpStubServer.Account.cs`: profile response contract, runtime status,
  persisted updates.
- `Persistence/LocalUserStore.cs`: selected-career profile projection and
  post-write invalidation; `LocalCouponService.cs`: raw entitlement reads.
- `Career/PlayerCharacterSnapshotFactory.cs`: shared APlay/profile snapshot
  construction.
- `Protocols/AccountTransportLivenessRegistry.cs` and
  `Protocols/MissionRuntimeRegistry.cs`: authoritative transport/mission state.

Installed client types inspected through their managed assemblies:

- `SRO.Client.ChatAndFriends.ChatChannel`, `LocalPlayerInfoManager`,
  `PlayerInfoResolver`, `PlayerInfoChangeNotificationManager`, `SROPlayerInfo`,
  `FriendsController`.
- `PushedMessageDispatcher.OnChannelParticipantListChanged` and
  `ChannelDescription`.
- Photon chat/friend DTOs and account-service `GetPlayerInfoResult`,
  `SinglePlayerInfoResult`, and `ResultCode`.

This document records observed contracts and behavior; no decompiled client
implementation or proprietary assets are included.
