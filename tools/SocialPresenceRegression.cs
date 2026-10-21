using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using PhotonProxy.ChatAndFriends.Client.DTOs;
using PhotonProxy.Common.ServiceCommunication;
using SRO.Core.Compatibility.Utilities;
using Shadowrun.LocalService.Core;
using Shadowrun.LocalService.Core.Http;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Protocols;

internal static class SocialPresenceRegression
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
            var measurePerformance = args.Length > 3 && bool.Parse(args[3]);
            if (measurePerformance) MeasurePerformance(args[1], bool.Parse(args[2]));
            else Run(args[1], bool.Parse(args[2]));
            Console.WriteLine("PASS: {0} ({1})", measurePerformance ? "social performance workload" : "social presence regressions", bool.Parse(args[2]) ? "SQLite" : "JSON");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MeasurePerformance(string dataDirectory, bool useSqlite)
    {
        const int accounts = 100;
        var options = new LocalServiceOptions
        {
            UseSqlite = useSqlite,
            DataDir = dataDirectory,
            SqliteDatabasePath = Path.Combine(dataDirectory, "localservice.sqlite"),
        };
        var logger = new RequestLogger(null, null);
        var store = new LocalUserStore(options, logger);
        var template = store.GetOrCreateCareer(Guid.NewGuid().ToString(), 0, true);
        template.PendingPersistenceCreation = false;
        var identities = new List<string>();
        var careers = new List<CareerSlot>();
        var accountRows = new Dictionary<string, object>();
        var profileRows = new Dictionary<string, object>();
        for (var i = 0; i < accounts; i++)
        {
            var identity = new Guid(i + 1, 0, 0, new byte[8]).ToString();
            identities.Add(identity);
            var career = CareerSlot.FromDictionary(template.ToDictionary());
            career.CharacterIdentifier = identity + ":0";
            career.CharacterName = "Runner-" + i.ToString("D3");
            careers.Add(career);
            accountRows[identity] = new Dictionary<string, object>
            {
                { "IdentityHash", identity }, { "DisplayName", "Launcher-" + i },
                { "LastCareerIndex", 0 }, { "Careers", new object[] { career.ToDictionary() } },
            };
            profileRows[identity] = new Dictionary<string, object>
            {
                { "SRO", new Dictionary<string, string>
                    {
                        { "LauncherDisplayName", "Launcher-" + i },
                        { "DisplayName", "Launcher-" + i + ";" + career.CharacterName },
                        { "CharacterName", career.CharacterName },
                        { "PlayerCharacter", SerializeSnapshot(identity, career) },
                    }
                },
            };
        }
        var accountRoot = new Dictionary<string, object> { { "SchemaVersion", 2 }, { "Accounts", accountRows } };
        typeof(LocalUserStore).GetMethod("SaveAccountStoreNoThrow", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(store, new object[] { accountRoot });
        var raw = new LocalPlayerInfoStore(options, logger, new SqliteLocalStore(options, logger));
        raw.MutateRootNoThrow(delegate(IDictionary root)
        {
            root.Clear();
            foreach (var row in profileRows) root[row.Key] = row.Value;
            return true;
        });

        var backend = useSqlite ? "SQLite" : "JSON";
        var startup = Stopwatch.StartNew();
        store = new LocalUserStore(options, logger);
        startup.Stop();
        Console.WriteLine("BENCH {0}: accounts={1}, startup={2:F2} ms", backend, accounts, startup.Elapsed.TotalMilliseconds);
        Benchmark(backend, "read-100", delegate()
        {
            for (var i = 0; i < 100; i++)
                Assert(store.GetPlayerInfo(identities[i % accounts], "SRO")["CharacterName"] == careers[i % accounts].CharacterName, "Benchmarked reads return correct names.");
        });
        Benchmark(backend, "search-20", delegate()
        {
            for (var i = 0; i < 20; i++)
                Assert(store.SearchPlayerInfo("SRO", careers[i].CharacterName).Count == 1, "Benchmarked searches match exactly one career.");
        });
        Benchmark(backend, "update-20", delegate()
        {
            for (var i = 0; i < 20; i++)
                store.SetPlayerInfo(identities[i], "SRO", new Dictionary<string, string> { { "Tag", Guid.NewGuid().ToString() } });
        });
        var uploadedProfiles = new List<Dictionary<string, string>>();
        for (var i = 0; i < 20; i++) uploadedProfiles.Add(raw.Get(identities[i], "SRO"));
        Benchmark(backend, "profile-upload-20", delegate()
        {
            for (var i = 0; i < 20; i++) store.SetPlayerInfo(identities[i], "SRO", uploadedProfiles[i]);
        });
        Benchmark(backend, "save-10", delegate()
        {
            for (var i = 0; i < 10; i++)
            {
                careers[i].Nuyen++;
                store.UpsertCareer(identities[i], careers[i]);
            }
        });
        Benchmark(backend, "select-10", delegate()
        {
            for (var i = 0; i < 10; i++) store.SetLastCareerIndex(identities[i], 0);
        });
        Benchmark(backend, "parallel-read-100", delegate()
        {
            System.Threading.Tasks.Parallel.For(0, 100, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
                delegate(int i) { Assert(store.GetPlayerInfo(identities[i % accounts], "SRO").Count > 0, "Concurrent profile reads succeed."); });
        });
    }

    private static void Benchmark(string backend, string operation, Action action)
    {
        action();
        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var timer = Stopwatch.StartNew();
            action();
            timer.Stop();
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        Console.WriteLine("BENCH {0}: {1} median={2:F2} ms", backend, operation, samples[2]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Run(string dataDirectory, bool useSqlite)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var options = new LocalServiceOptions
        {
            Host = "127.0.0.1",
            PhotonPort = port,
            UseSqlite = useSqlite,
            DataDir = dataDirectory,
            SqliteDatabasePath = Path.Combine(dataDirectory, "localservice.sqlite"),
        };
        var sessions = new ExpiringSessionIdentityMap();
        var logger = new RequestLogger(null, null);
        var store = new LocalUserStore(options, logger);
        var server = new PhotonProxyTcpStub(options, logger, store, sessions);
        using (var stop = new ManualResetEvent(false))
        {
            var connectedAccounts = new List<Guid>();
            Exception serverError = null;
            var thread = new Thread(delegate()
            {
                try { server.Run(stop); }
                catch (Exception ex) { serverError = ex; }
            });
            thread.IsBackground = true;
            thread.Start();
            try
            {
                using (var alice = new SocialClient(port, sessions))
                using (var bob = new SocialClient(port, sessions))
                {
                    connectedAccounts.Add(alice.AccountId);
                    connectedAccounts.Add(bob.AccountId);
                    Assert(IsOnline(alice.AccountId) && IsOnline(bob.AccountId), "Authenticated accounts are online.");
                    ValidateProfiles(options, store, sessions, alice.AccountId, bob.AccountId);
                    alice.Request(new JoinChannelRequest { ChannelName = "SRO_Default_Info" });
                    alice.Event<ChannelParticipantListChangedEventParameters>();
                    bob.Request(new JoinChannelRequest { ChannelName = "SRO_Default_Info" });
                    var joined = alice.Event<ChannelParticipantListChangedEventParameters>();
                    Assert(joined.Participant == bob.AccountId && joined.ChangeType == 1, "Join notification identifies the account and uses the client's Added value.");
                    var roster = (GetChannelParticipantsResponse)alice.Request(new GetChannelParticipantsRequest { ChannelName = "SRO_Default_Info" });
                    Assert(roster.Participants.Count == 2, "Presence roster includes both authenticated accounts.");
                    ValidateCareerProfiles(options, store, sessions, alice, bob);
                    ValidateNotificationDelivery(port, server, store, sessions, alice, bob);
                    ValidateAuthenticationDeadline(port);
                    Assert(IsOnline(alice.AccountId) && IsOnline(bob.AccountId), "Authenticated clients survive beyond the unauthenticated handshake deadline.");
                    bob.Request(new LeaveChannelRequest { ChannelName = "SRO_Default_Info" });
                    var left = alice.Event<ChannelParticipantListChangedEventParameters>(delegate(ChannelParticipantListChangedEventParameters e) { return e.Participant == bob.AccountId && e.ChangeType == 0; });
                    Assert(left.Participant == bob.AccountId && left.ChangeType == 0, "Leave notification identifies the departed account and uses the client's Deleted value.");
                    alice.Request(new JoinChannelRequest { ChannelName = "Global" });
                    bob.Request(new JoinChannelRequest { ChannelName = "Global" });
                    bob.Request(new SendMessageToChannelRequest { ChannelName = "Global", TextMessage = "bob-to-alice" });
                    var incoming = alice.Event<TextMessageEventParameters>(delegate(TextMessageEventParameters e) { return e.ChannelName == "Global"; });
                    Assert(incoming.Sender == bob.AccountId && incoming.Text == "bob-to-alice", "Remote chat preserves sender identity and text.");
                    alice.Request(new SendMessageToChannelRequest { ChannelName = "Global", TextMessage = "alice-to-bob" });
                    incoming = bob.Event<TextMessageEventParameters>(delegate(TextMessageEventParameters e) { return e.ChannelName == "Global"; });
                    Assert(incoming.Sender == alice.AccountId && incoming.Text == "alice-to-bob", "Chat is bidirectional.");
                    ValidateConcurrentFrames(alice, bob);
                    Thread.Sleep(2500);
                    Assert(IsOnline(alice.AccountId) && IsOnline(bob.AccountId), "Receive timeout must not disconnect idle clients.");
                    roster = (GetChannelParticipantsResponse)alice.Request(new GetChannelParticipantsRequest { ChannelName = "SRO_Default_Info" });
                    Assert(roster.Participants.Count == 1, "Requests still succeed after an idle receive timeout.");
                    var bobId = bob.AccountId;
                    bob.Request(new JoinChannelRequest { ChannelName = "SRO_Default_Info" });
                    alice.Event<ChannelParticipantListChangedEventParameters>(delegate(ChannelParticipantListChangedEventParameters e) { return e.ChannelName == "SRO_Default_Info" && e.Participant == bobId && e.ChangeType == 1; });
                    using (var replacement = new SocialClient(port, sessions, bobId))
                    {
                        replacement.Request(new JoinChannelRequest { ChannelName = "SRO_Default_Info" });
                        roster = (GetChannelParticipantsResponse)alice.Request(new GetChannelParticipantsRequest { ChannelName = "SRO_Default_Info" });
                        Assert(roster.Participants.Count == 2, "Overlapping connections for one account produce one roster entry.");
                        bob.Dispose();
                        roster = (GetChannelParticipantsResponse)replacement.Request(new GetChannelParticipantsRequest { ChannelName = "SRO_Default_Info" });
                        Assert(roster.Participants.Count == 2 && IsOnline(bobId), "Closing an old connection preserves its replacement's presence.");
                    }
                    var deadline = DateTime.UtcNow.AddSeconds(4);
                    while (IsOnline(bobId) && DateTime.UtcNow < deadline) Thread.Sleep(10);
                    Assert(!IsOnline(bobId), "Closing the final Photon socket clears transport liveness.");
                    left = alice.Event<ChannelParticipantListChangedEventParameters>(delegate(ChannelParticipantListChangedEventParameters e) { return e.ChannelName == "SRO_Default_Info" && e.Participant == bobId && e.ChangeType == 0; });
                    Assert(left.Participant == bobId && left.ChangeType == 0, "Final disconnect removes the account from the info-channel roster.");
                    roster = (GetChannelParticipantsResponse)alice.Request(new GetChannelParticipantsRequest { ChannelName = "Global" });
                    Assert(roster.Participants.Count == 1, "Closing a socket removes its channel membership.");
                }
            }
            finally
            {
                stop.Set();
                Assert(thread.Join(4000), "Photon listener stops.");
                foreach (var account in connectedAccounts)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(4);
                    while (IsOnline(account) && DateTime.UtcNow < deadline) Thread.Sleep(10);
                    Assert(!IsOnline(account), "Connection workers finish before their shutdown event is disposed.");
                }
            }
            if (serverError != null) throw new InvalidOperationException("Photon server failed.", serverError);
        }
    }

    private static bool IsOnline(Guid accountId)
    {
        var type = typeof(PhotonProxyTcpStub).Assembly.GetType("Shadowrun.LocalService.Core.Protocols.AccountTransportLivenessRegistry", true);
        return (bool)type.GetMethod("IsOnline").Invoke(null, new object[] { accountId });
    }

    private static void ValidateProfiles(LocalServiceOptions options, LocalUserStore store, ISessionIdentityMap sessions, Guid alice, Guid bob)
    {
        var http = new HttpStubServer(options, new RequestLogger(null, null), store, sessions, null);
        var peers = typeof(PhotonProxyTcpStub).Assembly.GetType("Shadowrun.LocalService.Core.Protocols.ConnectedPeerRegistry", true);
        var missions = typeof(PhotonProxyTcpStub).Assembly.GetType("Shadowrun.LocalService.Core.Protocols.MissionRuntimeRegistry", true);
        var peer = "social-regression-mission";
        peers.GetMethod("MarkConnected").Invoke(null, new object[] { peer });
        try
        {
            var started = missions.GetMethod("MarkSoloMissionStarted", new Type[] { typeof(string), typeof(Guid), typeof(string) });
            started.Invoke(null, new object[] { peer, alice, "regression-map" });
            var profiles = GetProfiles(http, alice, bob);
            Assert(bool.Parse(profiles[alice]["Online"]) && bool.Parse(profiles[alice]["InMission"]), "HTTP reports the connected mission participant as online and in mission.");
            Assert(bool.Parse(profiles[bob]["Online"]) && !bool.Parse(profiles[bob]["InMission"]), "HTTP distinguishes an online account outside a mission.");
            missions.GetMethod("MarkSoloMissionEnded").Invoke(null, new object[] { peer });
            Assert(!bool.Parse(GetProfiles(http, alice, bob)[alice]["InMission"]), "HTTP reflects mission end without relying on persisted client flags.");
        }
        finally
        {
            missions.GetMethod("MarkSoloMissionEnded").Invoke(null, new object[] { peer });
            peers.GetMethod("MarkDisconnected").Invoke(null, new object[] { peer });
        }
    }

    private static void ValidateCareerProfiles(LocalServiceOptions options, LocalUserStore store, ISessionIdentityMap sessions, SocialClient alice, SocialClient bob)
    {
        Console.WriteLine("Checking selected-career profiles and invalidation...");
        var identity = alice.AccountId.ToString();
        var oldCareer = store.GetOrCreateCareer(identity, 0, true);
        oldCareer.CharacterName = "Previous Career";
        oldCareer.PendingPersistenceCreation = false;
        store.UpsertCareer(identity, oldCareer);
        store.SetLastCareerIndex(identity, 0);
        var oldSnapshot = SerializeSnapshot(identity, oldCareer);
        store.SetPlayerInfo(identity, "SRO", new Dictionary<string, string>
        {
            { "DisplayName", "Launcher;Previous Career" },
            { "PlayerCharacter", oldSnapshot },
        });
        var nextCareer = store.GetOrCreateCareer(identity, 1, true);
        nextCareer.CharacterName = "Current Career";
        nextCareer.PendingPersistenceCreation = false;
        nextCareer.Nuyen = 123;
        nextCareer.Karma = 17;
        nextCareer.SpentKarma = 3;
        nextCareer.PrimaryWeaponQuality = 2;
        nextCareer.PrimaryWeaponFlavour = 4;
        store.UpsertCareer(identity, nextCareer);
        bob.Request(new GetChannelParticipantsRequest { ChannelName = "SRO_Default_Info" });
        bob.DiscardEvents();
        store.SetLastCareerIndex(identity, 1);
        var http = new HttpStubServer(options, new RequestLogger(null, null), store, sessions, null);
        var profiles = GetProfiles(http, alice.AccountId, bob.AccountId);
        AssertProfile(profiles[alice.AccountId], identity, nextCareer);
        var changed = bob.Event<TextMessageEventParameters>(delegate(TextMessageEventParameters e) { return e.ChannelName == "SRO_Default_Info"; });
        Assert(changed.Sender == alice.AccountId, "Career selection invalidates remote cached metadata using the changed account's info-channel sender.");

        ServeAccount(http, "SetPlayerInfo", new
        {
            IdentityHash = identity,
            GameName = "SRO",
            PlayerInfo = new Dictionary<string, string> { { "Tag", "publication-order-probe" } },
        });
        changed = bob.Event<TextMessageEventParameters>(delegate(TextMessageEventParameters e) { return e.ChannelName == "SRO_Default_Info"; });
        Assert(changed.Sender == alice.AccountId && store.GetPlayerInfo(identity, "SRO")["Tag"] == "publication-order-probe",
            "HTTP profile writes invalidate peers after the changed values are observable.");

        oldCareer.CharacterName = "Late Previous Career Save";
        store.UpsertCareer(identity, oldCareer);
        AssertProfile(GetProfiles(http, alice.AccountId, bob.AccountId)[alice.AccountId], identity, nextCareer);

        var setResult = ServeAccount(http, "SetPlayerInfo", new
        {
            IdentityHash = identity,
            GameName = "SRO",
            PlayerInfo = new Dictionary<string, string>
            {
                { "DisplayName", "Launcher;Previous Career" },
                { "PlayerCharacter", oldSnapshot },
            },
        });
        Assert((int)setResult["Code"] == 0, "A delayed previous-career upload succeeds without replacing the selected profile.");
        AssertProfile(GetProfiles(http, alice.AccountId, bob.AccountId)[alice.AccountId], identity, nextCareer);
        ServeAccount(http, "SetPlayerInfo", new
        {
            IdentityHash = identity,
            GameName = "SRO",
            PlayerInfo = new Dictionary<string, string> { { "PlayerCharacter", oldSnapshot } },
        });
        AssertProfile(GetProfiles(http, alice.AccountId, bob.AccountId)[alice.AccountId], identity, nextCareer);
        var persisted = new LocalPlayerInfoStore(options, new RequestLogger(null, null), new SqliteLocalStore(options, new RequestLogger(null, null)));
        AssertProfile(persisted.Get(identity, "SRO"), identity, nextCareer);

        persisted.Set(identity, "SRO", new Dictionary<string, string>
        {
            { "DisplayName", "Launcher;Previous Career" },
            { "CharacterName", "Previous Career" },
            { "PlayerCharacter", oldSnapshot },
        });
        persisted.Set(identity, "OtherGame", new Dictionary<string, string> { { "PlayerCharacter", "preserve-through-repair" } });
        var recreatedStore = new LocalUserStore(options, new RequestLogger(null, null));
        var recreatedHttp = new HttpStubServer(options, new RequestLogger(null, null), recreatedStore, sessions, null);
        var repairedSearch = recreatedStore.SearchPlayerInfo("SRO", nextCareer.CharacterName);
        Assert(repairedSearch.Count == 1 && repairedSearch[0].Key == identity, "Startup repairs historical searchable names before matching, without reselection.");
        Assert(recreatedStore.SearchPlayerInfo("SRO", "Previous Career").Count == 0, "Search no longer matches a historical career's stale name.");
        AssertProfile(repairedSearch[0].Value, identity, nextCareer);
        Assert(recreatedStore.GetPlayerInfo(identity, "OtherGame")["PlayerCharacter"] == "preserve-through-repair",
            "Startup repair preserves other games' stored data.");
        Assert(recreatedStore.GetPlayerInfo(identity, "SRO")["Tag"] == "publication-order-probe",
            "Startup repair preserves unrelated SRO profile fields.");
        AssertProfile(GetProfiles(recreatedHttp, alice.AccountId, bob.AccountId)[alice.AccountId], identity, nextCareer);
        store.SetLastCareerIndex(identity, 1);
        AssertProfile(persisted.Get(identity, "SRO"), identity, nextCareer);

        nextCareer.CharacterName = "Renamed Runn\u00e9r";
        store.UpsertCareer(identity, nextCareer);
        AssertProfile(GetProfiles(http, alice.AccountId, bob.AccountId)[alice.AccountId], identity, nextCareer);
        var search = store.SearchPlayerInfo("SRO", nextCareer.CharacterName);
        Assert(search.Count == 1 && search[0].Key == identity, "Player search returns the authoritative selected career.");
        AssertProfile(search[0].Value, identity, nextCareer);
        string renamedAccount;
        string renameError;
        Assert(store.TrySetDisplayName(identity, "Renamed Account", out renamedAccount, out renameError), "Account display-name updates succeed.");
        Assert(store.GetPlayerInfo(identity, "SRO")["DisplayName"] == "Renamed Account;" + nextCareer.CharacterName,
            "Account-name changes update persisted profile fields without relying on read-time projection.");
        Assert(store.SearchPlayerInfo("SRO", "Renamed Account").Count == 1, "An account-name change is searchable immediately.");
        AssertProfile(store.GetPlayerInfo(identity, " SRO "), identity, nextCareer);
        store.SetPlayerInfo(identity, "OtherGame", new Dictionary<string, string> { { "PlayerCharacter", "other-game-data" } });
        Assert(store.GetPlayerInfo(identity, "OtherGame")["PlayerCharacter"] == "other-game-data", "Non-SRO profile values are preserved.");
        var unselectedIdentity = Guid.NewGuid().ToString();
        store.SetPlayerInfo(unselectedIdentity, "SRO", new Dictionary<string, string> { { "DisplayName", "First value" } });
        store.SetPlayerInfo(unselectedIdentity, "SRO", new Dictionary<string, string> { { "DisplayName", "Updated value" } });
        Assert(store.GetPlayerInfo(unselectedIdentity, "SRO")["DisplayName"] == "Updated value", "Accounts without an occupied selected career retain normal profile update behavior.");
    }

    private static void ValidateNotificationDelivery(int port, PhotonProxyTcpStub server, LocalUserStore store,
        ExpiringSessionIdentityMap sessions, SocialClient alice, SocialClient bob)
    {
        using (var slow = new SocialClient(port, sessions))
        using (var writerHeld = new ManualResetEvent(false))
        using (var releaseWriter = new ManualResetEvent(false))
        {
            slow.Request(new JoinChannelRequest { ChannelName = "SRO_Default_Info" });
            var state = typeof(PhotonProxyTcpStub).GetField("_chatAndFriends", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
            var peers = (IDictionary)state.GetType().GetField("_peersByConnId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(state);
            object slowPeer = null;
            foreach (DictionaryEntry entry in peers)
            {
                if ((Guid)entry.Value.GetType().GetField("AccountId").GetValue(entry.Value) == slow.AccountId) slowPeer = entry.Value;
            }
            Assert(slowPeer != null, "The slow info-channel peer is registered.");
            var stream = slowPeer.GetType().GetField("Stream").GetValue(slowPeer);
            var blocker = new Thread(delegate()
            {
                lock (stream)
                {
                    writerHeld.Set();
                    releaseWriter.WaitOne(8000);
                }
            });
            blocker.IsBackground = true;
            blocker.Start();
            Assert(writerHeld.WaitOne(4000), "The slow peer's writer is occupied.");
            try
            {
                var elapsed = Stopwatch.StartNew();
                for (var i = 0; i < 32; i++)
                    store.SetPlayerInfo(alice.AccountId.ToString(), "SRO", new Dictionary<string, string> { { "Tag", "burst-" + i } });
                elapsed.Stop();
                Assert(elapsed.ElapsedMilliseconds < 1500, "Profile writes must not wait for a stalled info-channel writer.");
                var delivery = Stopwatch.StartNew();
                var changed = bob.Event<TextMessageEventParameters>(delegate(TextMessageEventParameters e) { return e.ChannelName == "SRO_Default_Info" && e.Sender == alice.AccountId; });
                Assert(changed.Sender == alice.AccountId && delivery.ElapsedMilliseconds < 1000, "A stalled peer does not hold up healthy recipients.");
                var pending = (HashSet<Guid>)slowPeer.GetType().GetField("PendingPlayerInfoChanges").GetValue(slowPeer);
                lock (pending) Assert(pending.Count <= 1, "Repeated changes coalesce to one pending account per slow peer.");
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (IsOnline(slow.AccountId) && DateTime.UtcNow < deadline) Thread.Sleep(10);
                Assert(!IsOnline(slow.AccountId), "A peer exceeding the bounded writer wait is disconnected instead of retaining an undeliverable queue.");
                Console.WriteLine("PERF: 32 profile writes with a stalled peer: {0} ms", elapsed.ElapsedMilliseconds);
            }
            finally
            {
                releaseWriter.Set();
                Assert(blocker.Join(4000), "The controlled blocked writer finishes.");
            }
        }
    }

    private static void ValidateAuthenticationDeadline(int port)
    {
        using (var idle = new TcpClient())
        using (var partial = new TcpClient())
        using (var pinging = new TcpClient())
        using (var stop = new ManualResetEvent(false))
        {
            idle.Connect(IPAddress.Loopback, port);
            partial.Connect(IPAddress.Loopback, port);
            pinging.Connect(IPAddress.Loopback, port);
            idle.ReceiveTimeout = partial.ReceiveTimeout = pinging.ReceiveTimeout = 14000;
            var header = new byte[] { 0xfb, 0, 0, 0xff, 0xff, 0, 0 };
            partial.GetStream().Write(header, 0, header.Length);
            var trickle = new Thread(delegate()
            {
                try
                {
                    while (!stop.WaitOne(100))
                    {
                        partial.GetStream().WriteByte(0);
                        var ping = new byte[] { 0xf0, 0, 0, 0, 0 };
                        pinging.GetStream().Write(ping, 0, ping.Length);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
            trickle.IsBackground = true;
            trickle.Start();
            try
            {
                Assert(idle.GetStream().ReadByte() == -1, "An idle unauthenticated socket expires.");
                Assert(partial.GetStream().ReadByte() == -1, "Partial-frame trickle traffic cannot extend authentication indefinitely.");
                var buffer = new byte[1024];
                while (pinging.GetStream().Read(buffer, 0, buffer.Length) > 0) { }
            }
            finally
            {
                stop.Set();
                Assert(trickle.Join(4000), "Unauthenticated traffic generator finishes.");
            }
        }
    }

    private static string SerializeSnapshot(string identity, CareerSlot career)
    {
        var method = typeof(APlayTcpStub).GetMethod("BuildPlayerCharacterSnapshotForSlot", BindingFlags.NonPublic | BindingFlags.Static);
        var snapshot = method.Invoke(null, new object[] { identity + ":" + career.Index, career.CharacterName, career });
        return ZipUtilities.Compress(JsonFxSerializerProvider.Current.Serialize<IPlayerCharacterSnapshot>((IPlayerCharacterSnapshot)snapshot));
    }

    private static void AssertProfile(Dictionary<string, string> info, string identity, CareerSlot career)
    {
        Assert(info["CharacterName"] == career.CharacterName && info["DisplayName"].EndsWith(";" + career.CharacterName), "Profile name strings describe the selected career.");
        var snapshot = JsonFxSerializerProvider.Current.Deserialize<PlayerCharacterSnapshot>(ZipUtilities.Decompress(info["PlayerCharacter"]));
        Assert(snapshot.CharacterName == career.CharacterName, "Chat snapshot name describes the selected career rather than the previous career.");
        Assert(snapshot.CharacterIdentifier == identity + ":" + career.Index, "Chat snapshot identifier matches the selected career.");
        Assert(snapshot.Nuyen == career.Nuyen && snapshot.PortraitPath == career.PortraitPath, "Profile projection retains selected career appearance and wallet data.");
        Assert(snapshot.Karma == career.Karma && snapshot.SpentKarma == career.SpentKarma, "The client codec preserves karma balances.");
        Assert(snapshot.PlayerCharacterInventory.PrimaryWeapon.Quality == career.PrimaryWeaponQuality
            && snapshot.PlayerCharacterInventory.PrimaryWeapon.FlavourIndex == career.PrimaryWeaponFlavour, "The client codec preserves selected-career equipment metadata.");
    }

    private static Dictionary<Guid, Dictionary<string, string>> GetProfiles(HttpStubServer http, Guid alice, Guid bob)
    {
        var request = new
        {
            IdentityHashes = new string[] { alice.ToString(), bob.ToString() },
            GameName = "SRO",
            Keys = new string[] { "Online", "InMission", "DisplayName", "CharacterName", "PlayerCharacter" },
        };
        return ParseProfiles(ServeAccount(http, "GetPlayerInfo", request), alice, bob);
    }

    private static IDictionary ServeAccount(HttpStubServer http, string operation, object request)
    {
        var json = new JavaScriptSerializer();
        var response = typeof(HttpStubServer).GetMethod("TryServeAccount", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(http, new object[] { "/AccountSystem/Accounts/PlayerActivity/" + operation, Encoding.UTF8.GetBytes(json.Serialize(request)) });
        var body = (byte[])response.GetType().GetField("BodyBytes").GetValue(response);
        return (IDictionary)json.DeserializeObject(Encoding.UTF8.GetString(body));
    }

    private static Dictionary<Guid, Dictionary<string, string>> ParseProfiles(IDictionary root, Guid alice, Guid bob)
    {
        Assert((int)root["Code"] == 0, "GetPlayerInfo succeeds.");
        var rows = (object[])root["PlayerInfoResults"];
        Assert(rows.Length == 2, "GetPlayerInfo returns every requested identity.");
        var result = new Dictionary<Guid, Dictionary<string, string>>();
        foreach (IDictionary row in rows)
        {
            Assert((int)row["Code"] == 0, "Each profile has the client's Ok result code.");
            var info = new Dictionary<string, string>();
            foreach (IDictionary item in (object[])row["PlayerInfo"]) info.Add((string)item["Key"], (string)item["Value"]);
            result.Add(new Guid((string)row["IdentityHash"]), info);
        }
        Assert(result.ContainsKey(alice) && result.ContainsKey(bob), "Profile response identities match the Photon sender identities.");
        return result;
    }

    private static void ValidateConcurrentFrames(SocialClient alice, SocialClient bob)
    {
        Exception sendError = null;
        const int count = 16;
        var text = new string('x', 16384);
        var sender = new Thread(delegate()
        {
            try
            {
                for (var i = 0; i < count; i++)
                    bob.Request(new SendMessageToChannelRequest { ChannelName = "Global", TextMessage = text + i });
            }
            catch (Exception ex) { sendError = ex; }
        });
        sender.IsBackground = true;
        sender.Start();
        for (var i = 0; i < count; i++)
        {
            alice.Request(new GetChannelParticipantsRequest { ChannelName = "Global" });
            var incoming = alice.Event<TextMessageEventParameters>(delegate(TextMessageEventParameters e) { return e.ChannelName == "Global"; });
            Assert(incoming.Sender == bob.AccountId && incoming.Text == text + i, "Concurrent responses and large events retain valid frames and payloads.");
        }
        Assert(sender.Join(4000), "Concurrent sender finishes.");
        if (sendError != null) throw new InvalidOperationException("Concurrent sender failed.", sendError);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SocialClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly ClientSerializer _serializer = new ClientSerializer();
        private readonly List<object> _events = new List<object>();
        private int _operationId;
        public readonly Guid AccountId;

        public SocialClient(int port, ExpiringSessionIdentityMap sessions)
            : this(port, sessions, Guid.NewGuid())
        {
        }

        public SocialClient(int port, ExpiringSessionIdentityMap sessions, Guid accountId)
        {
            AccountId = accountId;
            var session = Guid.NewGuid();
            sessions.SetIdentityForSession(session.ToString(), AccountId.ToString());
            _client = new TcpClient();
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (true)
            {
                try { _client.Connect(IPAddress.Loopback, port); break; }
                catch (SocketException)
                {
                    if (DateTime.UtcNow >= deadline) throw;
                    Thread.Sleep(20);
                }
            }
            _client.ReceiveTimeout = 4000;
            _stream = _client.GetStream();
            var response = Request(new ConnectRequest { AccountSystemSession = session }) as ConnectResponse;
            Assert(response != null && response.IdentityHash == AccountId, "ConnectRequest binds the requested session.");
        }

        public ISerializableMessage Request(ISerializableMessage request)
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                _serializer.Serialize(stream, request);
                bytes = stream.ToArray();
            }
            using (var payload = new MemoryStream())
            {
                payload.Write(new byte[] { 0xf3, 2, 0x64, 0, 3, 12, 0x69 }, 0, 7);
                WriteInt(payload, ++_operationId);
                payload.WriteByte(19); payload.WriteByte(0x73);
                var name = Encoding.UTF8.GetBytes(request.GetType().Name);
                payload.WriteByte((byte)(name.Length >> 8)); payload.WriteByte((byte)name.Length);
                payload.Write(name, 0, name.Length);
                payload.WriteByte(15); payload.WriteByte(0x78);
                WriteInt(payload, bytes.Length);
                payload.Write(bytes, 0, bytes.Length);
                SendFrame(payload.ToArray());
            }
            while (true)
            {
                var payload = ReadFrame();
                if (payload[1] == 4)
                {
                    _events.Add(Deserialize<PhotonClientMessage>(payload, 11).Payload);
                    continue;
                }
                var response = Deserialize<ServiceResponse>(payload, 14);
                Assert(response.OperationId == _operationId && string.IsNullOrEmpty(response.ErrorDescription), "Service request succeeds: " + request.GetType().Name);
                return response.Payload;
            }
        }

        public T Event<T>() where T : class
        {
            return Event<T>(delegate(T e) { return true; });
        }

        public void DiscardEvents()
        {
            _events.Clear();
        }

        public T Event<T>(Predicate<T> matches) where T : class
        {
            while (true)
            {
                for (var i = 0; i < _events.Count; i++)
                {
                    var result = _events[i] as T;
                    if (result == null || !matches(result)) continue;
                    _events.RemoveAt(i);
                    return result;
                }
                var payload = ReadFrame();
                Assert(payload[1] == 4, "Expected a Photon event.");
                _events.Add(Deserialize<PhotonClientMessage>(payload, 11).Payload);
            }
        }

        private T Deserialize<T>(byte[] bytes, int offset)
        {
            using (var stream = new MemoryStream(bytes, offset, bytes.Length - offset))
            {
                return (T)_serializer.Deserialize(stream, null, typeof(T));
            }
        }

        private void SendFrame(byte[] payload)
        {
            using (var stream = new MemoryStream())
            {
                stream.WriteByte(0xfb);
                WriteInt(stream, payload.Length + 7);
                stream.WriteByte(0); stream.WriteByte(0);
                stream.Write(payload, 0, payload.Length);
                var frame = stream.ToArray();
                _stream.Write(frame, 0, frame.Length);
            }
        }

        private byte[] ReadFrame()
        {
            var header = ReadExact(7);
            Assert(header[0] == 0xfb, "Photon frame boundary is intact.");
            var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
            Assert(length >= 7 && length < 1024 * 1024, "Photon frame length is valid.");
            return ReadExact(length - 7);
        }

        private byte[] ReadExact(int length)
        {
            var bytes = new byte[length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = _stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) throw new EndOfStreamException("Photon socket closed unexpectedly.");
                offset += count;
            }
            return bytes;
        }

        private static void WriteInt(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value);
        }

        public void Dispose()
        {
            _client.Close();
        }
    }
}
