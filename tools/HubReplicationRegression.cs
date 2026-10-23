using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay.Hub;
using Shadowrun.LocalService.Core;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Protocols;

internal static class HubReplicationRegression
{
    private static string _assemblyDirectory;

    public static int Main(string[] args)
    {
        _assemblyDirectory = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            var path = Path.Combine(_assemblyDirectory, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try
        {
            Run(args[1], bool.Parse(args[2]));
            Console.WriteLine("PASS: hub replication regressions ({0})", bool.Parse(args[2]) ? "SQLite" : "JSON");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Run(string dataDirectory, bool useSqlite)
    {
        var options = new LocalServiceOptions
        {
            UseSqlite = useSqlite,
            DataDir = dataDirectory,
            SqliteDatabasePath = Path.Combine(dataDirectory, "localservice.sqlite"),
        };
        var logger = new RequestLogger(null, null);
        var store = new LocalUserStore(options, logger);
        var sessions = new ExpiringSessionIdentityMap();
        var presence = new HubPresenceRegistry();
        var server = new APlayTcpStub(options, logger, store, sessions, null, presence);
        try
        {
            CheckConcurrentClaims();
            using (var first = new Peer(server, sessions, store, 1001))
            using (var second = new Peer(server, sessions, store, 1002))
            {
                first.RequestHub();
                first.Call(4, 2, Position(first.CharacterId, 3f, 4f));
                first.Barrier();
                second.RequestHub();
                second.Call(4, 2, Position(second.CharacterId, 5f, 6f));
                second.Barrier();
                first.Barrier();
                Assert(first.CountPending(4, 7) == 1 && second.CountPending(4, 7) == 1,
                    "Each peer receives exactly one incremental add for its remote avatar; counts="
                    + first.CountPending(4, 7) + "," + second.CountPending(4, 7) + ".");
                AssertRemoteAddition(server, presence, first.TakePending(4, 7), second.AccountId);
                AssertRemoteAddition(server, presence, second.TakePending(4, 7), first.AccountId);
                CheckFailedAnnouncement(server, presence, first.AccountId);
                CheckDetachedHubViews(server, presence, first.AccountId);
                first.ClearPending();
                second.ClearPending();

                first.RequestHub();
                first.RequestHub(true);
                first.Call(4, 2, Position(first.CharacterId, 3f, 4f));
                first.Barrier();
                second.Barrier();
                Assert(first.CountPending(4, 7) == 0 && second.CountPending(4, 7) == 0,
                    "Repeated same-hub requests/readiness never replay known avatars.");

                first.Call(4, 1, Position(first.CharacterId, 11f, 12f));
                first.Barrier();
                second.Barrier();
                var movement = second.TakePending(4, 4);
                var expectedMovement = HubMovementSerializer.Serialize(new[]
                {
                    new KeyValuePair<string, SRO.Core.Compatibility.Math.Vector2D>(
                        first.CharacterId, new SRO.Core.Compatibility.Math.Vector2D(11f, 12f)),
                });
                Assert(ReadString(movement.Data, 0) == expectedMovement,
                    "A ready peer receives movement for the canonical remote ID at the requested coordinates.");
                first.ClearPending();
                second.ClearPending();
                HubPresenceRegistry.Participant firstPresence;
                Assert(presence.TryGetParticipantForAccount(first.AccountId, out firstPresence), "First player remains registered.");
                var staleCareerId = first.AccountId.ToString() + ":5";
                first.Call(4, 1, Position(staleCareerId, 99f, 98f));
                first.Call(4, 2, Position(staleCareerId, 99f, 98f));
                first.Call(4, 1, Position(second.CharacterId, 97f, 96f));
                first.Call(4, 2, Position(second.CharacterId, 97f, 96f));
                first.Barrier();
                second.Barrier();
                HubPresenceRegistry.Participant afterStale;
                Assert(presence.TryGetParticipantForAccount(first.AccountId, out afterStale)
                    && afterStale.CharacterId == first.CharacterId && afterStale.X == 11f && afterStale.Y == 12f,
                    "Stale same-account career movement/entry cannot rekey presence or alter coordinates.");
                Assert(second.CountPending(4, 7) == 0 && second.CountPending(4, 4) == 0,
                    "Rejected stale-career commands emit no remote add/remove/movement.");
                Console.WriteLine("PASS: one add per avatar, same-hub refresh, remote movement, stale-career rejection");

                var before = store.GetOrCreateCareer(first.AccountId.ToString(), 0, false);
                var newSkin = before.SkinTextureIndex + 1;
                first.Call(3, 6, Strings("{\"SkinTextureIndexChange\":{\"NewIndex\":" + newSkin + "}}"));
                var changed = first.WaitFor(3, 33);
                var committed = store.GetOrCreateCareer(first.AccountId.ToString(), 0, false);
                var snapshot = (PlayerCharacterSnapshot)typeof(APlayTcpStub)
                    .GetMethod("BuildPlayerCharacterSnapshotForSlot", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { first.CharacterId, committed.CharacterName, committed });
                Assert(ReadString(changed.Data, 0) == PCSSerializer.SerializePlayerCharacterSnapshot(snapshot),
                    "Untyped field-6 edits return the committed character snapshot.");
                Assert(store.GetOrCreateCareer(first.AccountId.ToString(), 0, false).SkinTextureIndex == newSkin,
                    "Untyped field-6 edits persist through the structured parser.");
                var remoteChanged = second.WaitFor(4, 8);
                var mappedSnapshot = (PlayerCharacterSnapshot)typeof(APlayTcpStub)
                    .GetMethod("BuildMappedPlayerCharacterSnapshotForHub", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(server, new object[] { first.AccountId, first.CharacterId, committed.CharacterName, committed });
                Assert(ReadString(remoteChanged.Data, 0) == PCSSerializer.SerializePlayerCharacterSnapshot(mappedSnapshot),
                    "Remote hub snapshot event carries the authoritative committed cosmetics.");
                first.Barrier();
                second.Barrier();
                Assert(second.CountPending(4, 7) == 0, "Cosmetic updates never remove/re-add the remote avatar.");
                Console.WriteLine("PASS: untyped ChangeCharacter field-6 persistence and acknowledgement");

                var outfitChange = "{\"TypeName\":\"Cliffhanger.SRO.ServerClientCommons.Metagameplay.CharacterChangeCollection, Cliffhanger.SRO.ServerClientCommons\","
                    + "\"InventoryChanges\":[{\"Slot\":42,\"NewItem\":{\"ItemId\":\"outfit-regression\",\"InventoryKey\":30,\"Quality\":2,\"FlavourIndex\":3}}]}";
                first.Call(3, 6, Strings(outfitChange));
                first.WaitFor(3, 33);
                var outfitEvent = second.WaitFor(4, 8);
                var outfitted = store.GetOrCreateCareer(first.AccountId.ToString(), 0, false);
                Assert(outfitted.EquippedItems.ContainsKey("42")
                    && outfitted.EquippedItems["42"].ItemId == "outfit-regression"
                    && outfitted.EquippedItems["42"].Flavour == 3, "An inventory-only typed outfit edit persists.");
                var outfitSnapshot = (PlayerCharacterSnapshot)typeof(APlayTcpStub)
                    .GetMethod("BuildMappedPlayerCharacterSnapshotForHub", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(server, new object[] { first.AccountId, first.CharacterId, outfitted.CharacterName, outfitted });
                Assert(ReadString(outfitEvent.Data, 0) == PCSSerializer.SerializePlayerCharacterSnapshot(outfitSnapshot),
                    "Remote snapshot carries the equipped item, quality and flavour without a roster mutation.");
                Console.WriteLine("PASS: stock-style typed inventory-only cosmetic snapshot propagation");

                using (var replacement = new Peer(server, sessions, store, 1003, first.AccountId))
                {
                    replacement.RequestHub();
                    replacement.Call(4, 2, Position(replacement.CharacterId, 15f, 16f));
                    replacement.Barrier();
                    first.Dispose();
                    replacement.Barrier();
                    HubPresenceRegistry.Participant surviving;
                    Assert(presence.TryGetParticipantForAccount(replacement.AccountId, out surviving)
                        && surviving.CharacterId == replacement.CharacterId,
                        "Late cleanup of the retired socket preserves the replacement's presence.");
                    Assert(presence.SnapshotParticipants().Length == 2, "Replacement leaves one participant per active character.");
                    Console.WriteLine("PASS: replacement connection survives retired-session cleanup");
                }
                second.Barrier();
                Assert(second.CountPending(4, 7) == 1, "The final owner leaving removes exactly one remote avatar.");
                HubPresenceRegistry.Participant secondPresence;
                Assert(presence.TryGetParticipantForAccount(second.AccountId, out secondPresence), "Second participant remains.");
                var removal = second.TakePending(4, 7);
                Assert(ReadString(removal.Data, 0) == HubSerializer.SerializeHubStateUpdate(
                    HubStateUpdate.CreateForRemoveCharacter(first.CharacterId, secondPresence.HubId)),
                    "Disconnect removes the correct canonical character from the correct hub.");
                Console.WriteLine("PASS: single canonical disconnect removal");
            }
        }
        finally
        {
            ((Timer)typeof(APlayTcpStub).GetField("_missionCleanupTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server)).Dispose();
        }
    }

    private static void AssertRemoteAddition(APlayTcpStub server, HubPresenceRegistry registry, Frame frame, Guid remoteAccount)
    {
        HubPresenceRegistry.Participant participant;
        Assert(registry.TryGetParticipantForAccount(remoteAccount, out participant), "Remote participant exists.");
        var character = (HubPlayerCharacter)typeof(APlayTcpStub)
            .GetMethod("BuildHubPlayerCharacterFromParticipant", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(server, new object[] { participant });
        var expected = HubStateUpdate.CreateForCharacterAddtion(character, participant.HubId);
        Assert(ReadString(frame.Data, 0) == HubSerializer.SerializeHubStateUpdate(expected),
            "Incremental add contains exactly the remote canonical ID, snapshot and hub, never the local avatar.");
    }

    private static void CheckFailedAnnouncement(APlayTcpStub server, HubPresenceRegistry registry, Guid account)
    {
        HubPresenceRegistry.Participant participant;
        Assert(registry.TryGetParticipantForAccount(account, out participant), "Source participant exists.");
        var recipientAccount = Guid.NewGuid();
        registry.RegisterOrUpdate("failed-announcement", recipientAccount, recipientAccount.ToString(), 0,
            recipientAccount + ":0", "Failed recipient", participant.HubId, 0f, 0f);
        var deliveredHubs = (Dictionary<string, string>)typeof(APlayTcpStub)
            .GetField("_hubLastDeliveredHubByPeer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        deliveredHubs["failed-announcement"] = participant.HubId;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using (var client = new TcpClient())
            {
                client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                using (var accepted = listener.AcceptTcpClient())
                {
                    accepted.SendTimeout = 2000;
                    var closedStream = accepted.GetStream();
                    accepted.Client.Shutdown(SocketShutdown.Send);
                    var timer = Stopwatch.StartNew();
                    var written = (bool)typeof(APlayTcpStub)
                        .GetMethod("SendRawFrame", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(server, new object[] { closedStream, "failed-announcement", new byte[64 * 1024], "regression-shutdown-write" });
                    Assert(!written && !closedStream.CanWrite && timer.ElapsedMilliseconds < 6000,
                        "A failed socket write closes the transport promptly; sent="
                        + written + ", writable=" + closedStream.CanWrite + ", elapsedMs=" + timer.ElapsedMilliseconds + ".");
                    var targetType = typeof(APlayTcpStub).GetNestedType("HubPeerTarget", BindingFlags.NonPublic);
                    var target = Activator.CreateInstance(targetType, new object[] { "failed-announcement", closedStream });
                    var sent = (bool)typeof(APlayTcpStub)
                        .GetMethod("EnsureHubCharacterAnnouncedForTarget", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(server, new object[] { participant.HubId, participant, target, "regression-failed-write" });
                    var announced = (bool)typeof(APlayTcpStub)
                        .GetMethod("IsHubCharacterAnnounced", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(server, new object[] { "failed-announcement", participant.HubId, participant.CharacterId });
                    Assert(!sent && !announced, "A failed add write cannot commit an announcement or enable movement.");
                }
            }
        }
        finally
        {
            listener.Stop();
            registry.RemovePeer("failed-announcement");
            deliveredHubs.Remove("failed-announcement");
        }
        Console.WriteLine("PASS: failed write closes the transport without poisoning announcement dedupe");
    }

    private static void CheckDetachedHubViews(APlayTcpStub server, HubPresenceRegistry registry, Guid account)
    {
        HubPresenceRegistry.Participant participant;
        Assert(registry.TryGetParticipantForAccount(account, out participant), "Source participant exists.");
        var manager = typeof(APlayTcpStub)
            .GetField("_portedHubInstanceManager", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
        var instance = manager.GetType().GetMethod("RequestHubInstanceByHubId")
            .Invoke(manager, new object[] { participant.HubId });
        var stateProperty = instance.GetType().GetProperty("HubState");
        var detached = (HubState)stateProperty.GetValue(instance, null);
        var count = detached.PlayerCharacters.Count;
        var position = detached.PlayerCharacters[participant.CharacterId].CurrentPosition;
        detached.PlayerCharacters[participant.CharacterId].CurrentPosition = new SRO.Core.Compatibility.Math.Vector2D(999f, 999f);
        detached.PlayerCharacters.Clear();
        var current = (HubState)stateProperty.GetValue(instance, null);
        Assert(current.PlayerCharacters.Count == count
            && current.PlayerCharacters[participant.CharacterId].CurrentPosition.X == position.X
            && current.PlayerCharacters[participant.CharacterId].CurrentPosition.Y == position.Y,
            "Roster views and character positions are detached from authoritative mutable state.");
        Console.WriteLine("PASS: detached hub roster views");
    }

    private static void CheckConcurrentClaims()
    {
        var registry = new HubPresenceRegistry();
        var account = Guid.NewGuid();
        var character = account + ":0";
        using (var start = new ManualResetEvent(false))
        {
            var workers = new List<Thread>();
            for (var i = 0; i < 16; i++)
            {
                var peer = "claim-" + i;
                var worker = new Thread(delegate()
                {
                    start.WaitOne();
                    registry.RegisterOrUpdate(peer, account, account.ToString(), 0, character, "Runner", "Hub", 0f, 0f);
                });
                workers.Add(worker);
                worker.Start();
            }
            start.Set();
            foreach (var worker in workers) Assert(worker.Join(5000), "Concurrent claim completes.");
        }
        Assert(registry.SnapshotParticipants().Length == 1, "Concurrent claims retain exactly one character owner.");
        string winner;
        Assert(registry.TryGetPeerForCharacter(character, out winner), "The surviving character owner is indexed.");
        for (var i = 0; i < 16; i++)
        {
            var peer = "claim-" + i;
            if (peer != winner) registry.RemovePeer(peer);
        }
        Assert(registry.SnapshotParticipants().Length == 1
            && registry.TryGetPeerForCharacter(character, out winner),
            "Removing displaced peers cannot erase the winner's index.");
        Console.WriteLine("PASS: atomic concurrent character claims");
    }

    private sealed class Frame
    {
        public ulong Entity;
        public ushort Field;
        public byte[] Data;
    }

    private sealed class Peer : IDisposable
    {
        public readonly Guid AccountId = Guid.NewGuid();
        public readonly string CharacterId;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Thread _handler;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly List<Frame> _pending = new List<Frame>();
        private Exception _handlerError;
        private ulong _message;
        private bool _disposed;

        public Peer(APlayTcpStub server, ExpiringSessionIdentityMap sessions, LocalUserStore store, ulong entity, Guid? accountId = null)
        {
            if (accountId.HasValue) AccountId = accountId.Value;
            var slot = store.GetOrCreateCareer(AccountId.ToString(), 0, !accountId.HasValue);
            slot.PendingPersistenceCreation = false;
            slot.IsOccupied = true;
            slot.CharacterName = "Runner-" + entity;
            store.UpsertCareer(AccountId.ToString(), slot);
            CharacterId = slot.CharacterIdentifier;
            var session = Guid.NewGuid().ToString();
            sessions.SetIdentityForSession(session, AccountId.ToString());
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                _client = new TcpClient();
                _client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var accepted = listener.AcceptTcpClient();
                _handler = new Thread(delegate()
                {
                    try
                    {
                        typeof(APlayTcpStub).GetMethod("HandleClient", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(server, new object[] { accepted, _stop });
                    }
                    catch (Exception ex) { _handlerError = ex; }
                });
                _handler.IsBackground = true;
                _handler.Start();
            }
            finally { listener.Stop(); }
            _stream = _client.GetStream();
            Call(entity, 3, Strings(session, "RegularConnect"));
            WaitFor(entity, 4);
            Call(2, 11, BitConverter.GetBytes(0));
            WaitFor(3, 38);
        }

        public void Call(ulong entity, ushort field, byte[] data)
        {
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write((byte)3);
                writer.Write((uint)1);
                writer.Write(15 + data.Length);
                writer.Write((byte)1);
                writer.Write(entity);
                writer.Write(field);
                writer.Write(data.Length);
                writer.Write(data);
                writer.Write(++_message);
                var core = memory.ToArray();
                var decoded = new byte[core.Length + 4];
                Buffer.BlockCopy(BitConverter.GetBytes(core.Length), 0, decoded, 0, 4);
                Buffer.BlockCopy(core, 0, decoded, 4, core.Length);
                var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(decoded) + "\0");
                _stream.Write(encoded, 0, encoded.Length);
            }
        }

        public Frame WaitFor(ulong entity, ushort field)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 5000)
            {
                for (var i = 0; i < _pending.Count; i++)
                {
                    var item = _pending[i];
                    if (item.Entity == entity && item.Field == field)
                    {
                        _pending.RemoveAt(i);
                        return item;
                    }
                }
                if (_handlerError != null) throw new Exception("APlay handler failed.", _handlerError);
                _stream.ReadTimeout = Math.Max(1, 5000 - (int)timer.ElapsedMilliseconds);
                var encoded = new StringBuilder();
                int value;
                while ((value = _stream.ReadByte()) > 0) encoded.Append((char)value);
                if (value < 0) throw new EndOfStreamException("APlay peer closed.");
                var decoded = Convert.FromBase64String(encoded.ToString());
                if (decoded.Length < 28 || decoded[4] != 2 || decoded[13] != 5) continue;
                var dataLength = BitConverter.ToInt32(decoded, 24);
                var payload = new byte[dataLength];
                Buffer.BlockCopy(decoded, 28, payload, 0, dataLength);
                _pending.Add(new Frame
                {
                    Entity = BitConverter.ToUInt64(decoded, 14),
                    Field = BitConverter.ToUInt16(decoded, 22),
                    Data = payload,
                });
            }
            throw new Exception("Timed out waiting for entity " + entity + ", field " + field + ".");
        }

        public void RequestHub(bool storyHubForSelf = false)
        {
            if (storyHubForSelf) Call(3, 2, Strings(CharacterId, AccountId.ToString()));
            else Call(3, 1, Strings("{\"Content\":{\"TypeName\":\"RequestCurrentStorylineHubMessage\"}}"));
            var handoff = WaitFor(3, 37);
            var serialized = ReadString(handoff.Data, 8);
            // Hub state headers are followed by the character count. The server sends
            // only a handoff; incremental field-7 events own the entire remote roster.
            var bytes = Convert.FromBase64String(serialized);
            var offset = 0;
            for (var i = 0; i < 2; i++)
            {
                offset += 4 + BitConverter.ToInt32(bytes, offset) * 2;
            }
            Assert(bytes.Length == offset + 1 && bytes[offset] == 0,
                "Full hub handoff must not also contain remote avatars replayed incrementally.");
        }

        public void Barrier()
        {
            _pending.RemoveAll(delegate(Frame item) { return item.Entity == 3 && item.Field == 26; });
            Call(3, 5, new byte[0]);
            WaitFor(3, 26);
        }

        public int CountPending(ulong entity, ushort field)
        {
            return _pending.FindAll(delegate(Frame item) { return item.Entity == entity && item.Field == field; }).Count;
        }

        public Frame TakePending(ulong entity, ushort field)
        {
            var index = _pending.FindIndex(delegate(Frame item) { return item.Entity == entity && item.Field == field; });
            Assert(index >= 0, "Expected queued event for entity " + entity + ", field " + field + ".");
            var frame = _pending[index];
            _pending.RemoveAt(index);
            return frame;
        }

        public void ClearPending() { _pending.Clear(); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Set();
            _client.Close();
            Assert(_handler.Join(5000), "APlay handler terminates on disconnect.");
            // Keepalive callbacks still reference this signaled handle; let GC
            // release it once those callbacks have observed shutdown and exited.
            if (_handlerError != null) throw new Exception("APlay handler failed.", _handlerError);
        }
    }

    private static byte[] Strings(params string[] values)
    {
        using (var memory = new MemoryStream())
        using (var writer = new BinaryWriter(memory))
        {
            foreach (var value in values)
            {
                writer.Write(value.Length);
                writer.Write(Encoding.Unicode.GetBytes(value));
            }
            return memory.ToArray();
        }
    }

    private static string ReadString(byte[] data, int offset)
    {
        return Encoding.Unicode.GetString(data, offset + 4, BitConverter.ToInt32(data, offset) * 2);
    }

    private static byte[] Position(string characterId, float x, float y)
    {
        var identifier = Strings(characterId);
        var payload = new byte[identifier.Length + 8];
        Buffer.BlockCopy(identifier, 0, payload, 0, identifier.Length);
        Buffer.BlockCopy(BitConverter.GetBytes(x), 0, payload, identifier.Length, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(y), 0, payload, identifier.Length + 4, 4);
        return payload;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
