using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Shadowrun.LocalService.Core.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;
using Shadowrun.LocalService.Core.Protocols;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed class LocalDebugHttpServer
    {
        private static readonly JavaScriptSerializer Json = CreateSerializer();

        private readonly LocalServiceOptions _options;
        private readonly RequestLogger _logger;
        private readonly LocalUserStore _userStore;
        private readonly ActiveMissionDebugRegistry _missionRegistry;
        private readonly CharacterStatePushBroker _characterStatePushBroker;
        private readonly object _snapshotLock = new object();
        private readonly Dictionary<string, IDictionary> _careerSnapshots = new Dictionary<string, IDictionary>(StringComparer.OrdinalIgnoreCase);

        private int _listeningPort;

        public LocalDebugHttpServer(
            LocalServiceOptions options,
            RequestLogger logger,
            LocalUserStore userStore,
            ActiveMissionDebugRegistry missionRegistry,
            CharacterStatePushBroker characterStatePushBroker)
        {
            _options = options;
            _logger = logger;
            _userStore = userStore;
            _missionRegistry = missionRegistry ?? new ActiveMissionDebugRegistry();
            _characterStatePushBroker = characterStatePushBroker;
        }

        public int ListeningPort
        {
            get { return _listeningPort; }
        }

        public void Run(ManualResetEvent stopEvent)
        {
            if (_options == null || !_options.EnableDebugApi)
            {
                return;
            }

            // Dedicated loopback-only listener: NEVER binds to 0.0.0.0 or any external interface.
            var port = _options.DebugApiPort >= 0 ? _options.DebugApiPort : 18080;
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            var localEp = listener.LocalEndpoint as IPEndPoint;
            _listeningPort = localEp != null ? localEp.Port : port;

            ThreadPool.QueueUserWorkItem(delegate
            {
                stopEvent.WaitOne();
                try { listener.Stop(); }
                catch { }
            });

            if (_logger != null)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "debug-http",
                    status = "listening",
                    host = "127.0.0.1",
                    port = _listeningPort,
                    tokenRequired = !IsNullOrWhiteSpace(_options.DebugApiToken),
                });
            }

            Console.WriteLine("[localservice-cs] debug API listening on http://127.0.0.1:{0}", _listeningPort);

            while (!stopEvent.WaitOne(0))
            {
                TcpClient client = null;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (stopEvent.WaitOne(0))
                    {
                        break;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (client == null)
                {
                    continue;
                }

                ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    HandleClient((TcpClient)state);
                }, client);
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                try
                {
                    var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                    if (remoteEp == null || !IPAddress.IsLoopback(remoteEp.Address))
                    {
                        using (var rejectStream = client.GetStream())
                        {
                            WriteJsonResponse(rejectStream, 403, new { ok = false, error = "forbidden", message = "Debug API accepts loopback connections only." });
                        }
                        return;
                    }

                    using (var stream = client.GetStream())
                    {
                        DebugHttpRequest req;
                        try
                        {
                            req = ReadRequest(stream);
                        }
                        catch (Exception ex)
                        {
                            WriteJsonResponse(stream, 400, new { ok = false, error = "bad_request", message = ex.Message });
                            return;
                        }

                        if (req == null)
                        {
                            return;
                        }

                        if (!IsNullOrWhiteSpace(_options.DebugApiToken))
                        {
                            string suppliedToken;
                            if (!req.Headers.TryGetValue("x-debug-token", out suppliedToken)
                                || !string.Equals(suppliedToken, _options.DebugApiToken, StringComparison.Ordinal))
                            {
                                WriteJsonResponse(stream, 401, new { ok = false, error = "unauthorized", message = "Missing or invalid X-Debug-Token header." });
                                return;
                            }
                        }

                        int statusCode;
                        object payload;
                        try
                        {
                            Dispatch(req, out statusCode, out payload);
                        }
                        catch (Exception ex)
                        {
                            statusCode = 500;
                            payload = new { ok = false, error = "internal_error", message = ex.Message };
                        }

                        WriteJsonResponse(stream, statusCode, payload);
                    }
                }
                catch
                {
                }
            }
        }

        private void Dispatch(DebugHttpRequest req, out int statusCode, out object payload)
        {
            var method = (req.Method ?? "GET").ToUpperInvariant();
            var path = (req.Path ?? "/").TrimEnd('/');
            if (path.Length == 0)
            {
                path = "/";
            }

            if (string.Equals(path, "/debug/v1/health", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleHealth(out statusCode, out payload);
                return;
            }

            if (string.Equals(path, "/debug/v1/missions", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleListMissions(out statusCode, out payload);
                return;
            }

            if (path.StartsWith("/debug/v1/missions/", StringComparison.OrdinalIgnoreCase))
            {
                HandleMissionRoute(method, path, req.QueryParams, out statusCode, out payload);
                return;
            }

            if (string.Equals(path, "/debug/v1/seeds/next", StringComparison.OrdinalIgnoreCase))
            {
                HandleNextSeedRoute(method, req.BodyText, out statusCode, out payload);
                return;
            }

            if (string.Equals(path, "/debug/v1/accounts", StringComparison.OrdinalIgnoreCase) && method == "GET")
            {
                HandleListAccounts(out statusCode, out payload);
                return;
            }

            if (path.StartsWith("/debug/v1/accounts/", StringComparison.OrdinalIgnoreCase))
            {
                HandleAccountRoute(method, path, req.BodyText, out statusCode, out payload);
                return;
            }

            statusCode = 404;
            payload = new { ok = false, error = "not_found", message = "Unknown debug endpoint: " + method + " " + path };
        }

        private void HandleHealth(out int statusCode, out object payload)
        {
            bool sticky;
            var nextSeeds = _missionRegistry.GetNextSeedOverrideSnapshot(out sticky);
            var missions = _missionRegistry.ListMissions();
            var missionSummaries = new object[missions.Length];
            for (var i = 0; i < missions.Length; i++)
            {
                missionSummaries[i] = BuildMissionSummary(missions[i]);
            }

            statusCode = 200;
            payload = new
            {
                ok = true,
                version = "v1",
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                port = _listeningPort,
                persistenceBackend = _options.UseSqlite ? "Sqlite" : "Json",
                useFixedMissionSeeds = _options.UseFixedMissionSeeds,
                nextMissionSeedOverride = nextSeeds,
                nextMissionSeedSticky = sticky,
                activeMissionCount = missionSummaries.Length,
                activeMissions = missionSummaries,
            };
        }

        private void HandleListMissions(out int statusCode, out object payload)
        {
            var missions = _missionRegistry.ListMissions();
            var list = new object[missions.Length];
            for (var i = 0; i < missions.Length; i++)
            {
                list[i] = BuildMissionSummary(missions[i]);
            }

            statusCode = 200;
            payload = new
            {
                ok = true,
                count = list.Length,
                missions = list,
            };
        }

        private static object BuildMissionSummary(ActiveMissionDebugRegistry.ActiveMissionEntry entry)
        {
            if (entry == null)
            {
                return null;
            }
            var participantGuids = entry.ParticipantAccountIds;
            var participantAccountIds = new string[participantGuids != null ? participantGuids.Length : 0];
            if (participantGuids != null)
            {
                for (var i = 0; i < participantGuids.Length; i++)
                {
                    participantAccountIds[i] = participantGuids[i].ToString("D");
                }
            }
            return new
            {
                sessionId = entry.SessionId,
                mode = entry.Mode,
                peer = entry.Peer,
                coopGroupName = entry.CoopGroupName,
                accountId = entry.AccountId != Guid.Empty ? entry.AccountId.ToString("D") : string.Empty,
                participantAccountIds = participantAccountIds,
                careerIndex = entry.CareerIndex,
                mapName = entry.MapName,
                seeds = new uint[] { entry.Seed0, entry.Seed1, entry.Seed2, entry.Seed3 },
                isStarted = entry.Simulation != null && entry.Simulation.IsMissionStarted,
                isStopped = entry.Simulation != null && entry.Simulation.IsMissionStopped,
                createdUtc = entry.CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
            };
        }

        private void HandleMissionRoute(string method, string path, Dictionary<string, string> query, out int statusCode, out object payload)
        {
            if (method != "GET")
            {
                statusCode = 405;
                payload = new { ok = false, error = "method_not_allowed", message = "Mission inspection endpoints require GET." };
                return;
            }

            // /debug/v1/missions/{id}/{action}
            var rest = path.Substring("/debug/v1/missions/".Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0 || slash >= rest.Length - 1)
            {
                statusCode = 404;
                payload = new { ok = false, error = "not_found", message = "Expected /debug/v1/missions/{id}/{state|last-command|trace}." };
                return;
            }

            string sessionKey;
            if (!TryUnescapeDataString(rest.Substring(0, slash), out sessionKey))
            {
                statusCode = 400;
                payload = new { ok = false, error = "bad_request", message = "Malformed percent-encoding in mission session id." };
                return;
            }
            var action = rest.Substring(slash + 1).ToLowerInvariant();

            ActiveMissionDebugRegistry.ActiveMissionEntry entry;
            if (!_missionRegistry.TryResolveMission(sessionKey, out entry) || entry == null || entry.Simulation == null)
            {
                statusCode = 404;
                payload = new { ok = false, error = "mission_not_found", message = "No active mission matched '" + sessionKey + "'." };
                return;
            }

            if (action == "state")
            {
                var includeInactive = GetQueryBool(query, "includeInactive", true);
                var includeInteractives = GetQueryBool(query, "includeInteractives", true);
                var includeStatusValues = GetQueryBool(query, "includeStatusValues", false);
                lock (entry.SyncRoot)
                {
                    payload = entry.Simulation.BuildDebugStateSnapshot(
                        entry.SessionId,
                        entry.Seed0,
                        entry.Seed1,
                        entry.Seed2,
                        entry.Seed3,
                        includeInactive,
                        includeInteractives,
                        includeStatusValues);
                }
                statusCode = 200;
                return;
            }

            if (action == "last-command")
            {
                object lastEntry;
                lock (entry.SyncRoot)
                {
                    lastEntry = entry.Simulation.GetLastDebugCommandTrace();
                }
                statusCode = 200;
                payload = new { ok = true, sessionId = entry.SessionId, entry = lastEntry };
                return;
            }

            if (action == "trace")
            {
                var limit = GetQueryInt(query, "limit", 50);
                object[] entries;
                lock (entry.SyncRoot)
                {
                    entries = entry.Simulation.GetRecentDebugCommandTraces(limit);
                }
                statusCode = 200;
                payload = new { ok = true, sessionId = entry.SessionId, count = entries.Length, entries = entries };
                return;
            }

            statusCode = 404;
            payload = new { ok = false, error = "not_found", message = "Unknown mission debug action: " + action };
        }

        private void HandleNextSeedRoute(string method, string bodyText, out int statusCode, out object payload)
        {
            if (method == "DELETE")
            {
                _missionRegistry.ClearNextSeedOverride();
                statusCode = 200;
                payload = new { ok = true, cleared = true };
                return;
            }

            if (method != "PUT" && method != "POST")
            {
                statusCode = 405;
                payload = new { ok = false, error = "method_not_allowed", message = "Use PUT/POST to set or DELETE to clear next seeds." };
                return;
            }

            var body = ParseJsonDict(bodyText);
            if (body == null)
            {
                statusCode = 400;
                payload = new { ok = false, error = "invalid_json", message = "Expected JSON object body." };
                return;
            }

            var mode = GetString(body, "mode");
            if (string.Equals(mode, "random", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "clear", StringComparison.OrdinalIgnoreCase))
            {
                _missionRegistry.ClearNextSeedOverride();
                statusCode = 200;
                payload = new { ok = true, mode = "cleared" };
                return;
            }

            var sticky = GetBool(body, "sticky", false);
            if (string.Equals(mode, "fixed_default", StringComparison.OrdinalIgnoreCase))
            {
                _missionRegistry.SetNextSeedOverride(0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u, sticky);
                statusCode = 200;
                payload = new { ok = true, seeds = new uint[] { 0x11111111u, 0x22222222u, 0x33333333u, 0x44444444u }, sticky = sticky };
                return;
            }

            var seedsArr = GetArray(body, "seeds");
            if (seedsArr == null || seedsArr.Length != 4)
            {
                statusCode = 400;
                payload = new { ok = false, error = "invalid_seeds", message = "Provide 'seeds': [s0, s1, s2, s3] (4 unsigned 32-bit integers) or 'mode': 'fixed_default'|'random'." };
                return;
            }

            uint s0, s1, s2, s3;
            try
            {
                s0 = Convert.ToUInt32(seedsArr[0], CultureInfo.InvariantCulture);
                s1 = Convert.ToUInt32(seedsArr[1], CultureInfo.InvariantCulture);
                s2 = Convert.ToUInt32(seedsArr[2], CultureInfo.InvariantCulture);
                s3 = Convert.ToUInt32(seedsArr[3], CultureInfo.InvariantCulture);
            }
            catch
            {
                statusCode = 400;
                payload = new { ok = false, error = "invalid_seeds", message = "Seeds must be valid uint32 integers." };
                return;
            }

            _missionRegistry.SetNextSeedOverride(s0, s1, s2, s3, sticky);
            statusCode = 200;
            payload = new { ok = true, seeds = new uint[] { s0, s1, s2, s3 }, sticky = sticky };
        }

        private void HandleListAccounts(out int statusCode, out object payload)
        {
            var identities = _userStore != null ? _userStore.ListAccountIdentitiesForDebug() : new string[0];
            var results = new List<object>(identities.Length);
            for (var i = 0; i < identities.Length; i++)
            {
                var id = identities[i];
                var careers = _userStore.GetCareers(id);
                Guid guid;
                TryParseGuid(id, out guid);
                results.Add(new
                {
                    accountId = id,
                    careerCount = careers != null ? careers.Count : 0,
                    occupiedCareers = careers != null ? CountOccupied(careers) : 0,
                    inMission = guid != Guid.Empty && _missionRegistry.IsAccountInMission(guid),
                });
            }

            statusCode = 200;
            payload = new { ok = true, count = results.Count, accounts = results.ToArray() };
        }

        private static int CountOccupied(List<CareerSlot> careers)
        {
            var n = 0;
            for (var i = 0; i < careers.Count; i++)
            {
                if (careers[i] != null && careers[i].IsOccupied)
                {
                    n++;
                }
            }
            return n;
        }

        private void HandleAccountRoute(string method, string path, string bodyText, out int statusCode, out object payload)
        {
            // /debug/v1/accounts/{accountId}/careers/{slot}[/{action}]
            var rest = path.Substring("/debug/v1/accounts/".Length);
            var parts = rest.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || !string.Equals(parts[1], "careers", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = 404;
                payload = new { ok = false, error = "not_found", message = "Expected /debug/v1/accounts/{accountId}/careers/{slot}[/preset|/snapshot|/restore]." };
                return;
            }

            string rawAccountId;
            if (!TryUnescapeDataString(parts[0], out rawAccountId))
            {
                statusCode = 400;
                payload = new { ok = false, error = "bad_request", message = "Malformed percent-encoding in account id." };
                return;
            }
            int slotIndex;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out slotIndex) || slotIndex < 0 || slotIndex > 15)
            {
                statusCode = 400;
                payload = new { ok = false, error = "invalid_slot", message = "Career slot index must be an integer between 0 and 15." };
                return;
            }

            var identityHash = ResolveAccountIdentity(rawAccountId);
            if (IsNullOrWhiteSpace(identityHash))
            {
                statusCode = 404;
                payload = new { ok = false, error = "account_not_found", message = "Could not resolve account '" + rawAccountId + "'." };
                return;
            }

            var subAction = parts.Length >= 4 ? parts[3].ToLowerInvariant() : string.Empty;
            if (subAction == "snapshot" && method == "POST")
            {
                HandleCareerSnapshot(identityHash, slotIndex, bodyText, out statusCode, out payload);
                return;
            }
            if (subAction == "restore" && method == "POST")
            {
                HandleCareerRestore(identityHash, slotIndex, bodyText, out statusCode, out payload);
                return;
            }
            if (subAction == "preset" && method == "POST")
            {
                HandleCareerPreset(identityHash, slotIndex, bodyText, out statusCode, out payload);
                return;
            }

            if (parts.Length > 3)
            {
                statusCode = 404;
                payload = new { ok = false, error = "not_found", message = "Unknown career debug action: " + subAction };
                return;
            }

            if (method == "GET")
            {
                var slot = _userStore.GetOrCreateCareer(identityHash, slotIndex, false);
                statusCode = 200;
                payload = new
                {
                    ok = true,
                    accountId = identityHash,
                    slotIndex = slotIndex,
                    career = slot != null ? slot.ToDictionary() : null,
                };
                return;
            }

            if (method == "PATCH" || method == "PUT")
            {
                var body = ParseJsonDict(bodyText);
                if (body == null)
                {
                    statusCode = 400;
                    payload = new { ok = false, error = "invalid_json", message = "Expected JSON object body." };
                    return;
                }

                Guid accountGuid;
                if (!CheckAccountNotInMission(identityHash, body, out accountGuid, out statusCode, out payload))
                {
                    return;
                }

                var slot = _userStore.GetOrCreateCareer(identityHash, slotIndex, true);
                if (slot == null)
                {
                    statusCode = 500;
                    payload = new { ok = false, error = "career_load_failed", message = "Failed to load or create career slot." };
                    return;
                }

                string validationError;
                if (!ApplyCareerPatch(slot, body, method == "PUT", out validationError))
                {
                    statusCode = 400;
                    payload = new { ok = false, error = "validation_failed", message = validationError };
                    return;
                }

                _userStore.UpsertCareer(identityHash, slot);
                var pushToClient = GetBool(body, "pushToConnectedClient", true);
                if (pushToClient && _characterStatePushBroker != null && accountGuid != Guid.Empty)
                {
                    try { _characterStatePushBroker.Enqueue(accountGuid, CharacterStatePushPaths.Wallet | CharacterStatePushPaths.Inventory | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries); }
                    catch { }
                }

                statusCode = 200;
                payload = new
                {
                    ok = true,
                    accountId = identityHash,
                    slotIndex = slotIndex,
                    career = slot.ToDictionary(),
                };
                return;
            }

            statusCode = 405;
            payload = new { ok = false, error = "method_not_allowed", message = "Supported methods: GET, PATCH, PUT." };
        }

        private void HandleCareerSnapshot(string identityHash, int slotIndex, string bodyText, out int statusCode, out object payload)
        {
            var body = ParseJsonDict(bodyText) ?? new Dictionary<string, object>();
            var name = GetString(body, "name");
            if (IsNullOrWhiteSpace(name))
            {
                name = "default";
            }

            var slot = _userStore.GetOrCreateCareer(identityHash, slotIndex, false);
            var key = identityHash + ":" + slotIndex.ToString(CultureInfo.InvariantCulture) + ":" + name.Trim();
            var cloned = slot != null ? slot.ToDictionary() : null;
            lock (_snapshotLock)
            {
                _careerSnapshots[key] = cloned;
            }

            statusCode = 200;
            payload = new { ok = true, accountId = identityHash, slotIndex = slotIndex, snapshotName = name.Trim() };
        }

        private bool CheckAccountNotInMission(string identityHash, IDictionary body, out Guid accountGuid, out int statusCode, out object payload)
        {
            TryParseGuid(identityHash, out accountGuid);
            var force = body != null && GetBool(body, "force", false);
            if (!force && accountGuid != Guid.Empty && _missionRegistry != null && _missionRegistry.IsAccountInMission(accountGuid))
            {
                statusCode = 409;
                payload = new { ok = false, error = "account_in_mission", message = "Account is currently in an active mission. Pass 'force': true to mutate anyway." };
                return false;
            }

            statusCode = 200;
            payload = null;
            return true;
        }

        private void HandleCareerRestore(string identityHash, int slotIndex, string bodyText, out int statusCode, out object payload)
        {
            var body = ParseJsonDict(bodyText) ?? new Dictionary<string, object>();
            Guid accountGuid;
            if (!CheckAccountNotInMission(identityHash, body, out accountGuid, out statusCode, out payload))
            {
                return;
            }

            var name = GetString(body, "name");
            if (IsNullOrWhiteSpace(name))
            {
                name = "default";
            }

            var key = identityHash + ":" + slotIndex.ToString(CultureInfo.InvariantCulture) + ":" + name.Trim();
            IDictionary saved;
            lock (_snapshotLock)
            {
                if (!_careerSnapshots.TryGetValue(key, out saved) || saved == null)
                {
                    statusCode = 404;
                    payload = new { ok = false, error = "snapshot_not_found", message = "No saved snapshot named '" + name + "'." };
                    return;
                }
            }

            var restored = CareerSlot.FromDictionary(saved);
            if (restored == null)
            {
                statusCode = 500;
                payload = new { ok = false, error = "restore_failed", message = "Failed to deserialize saved snapshot." };
                return;
            }

            restored.Index = slotIndex;
            restored.CharacterIdentifier = identityHash + ":" + slotIndex.ToString(CultureInfo.InvariantCulture);
            _userStore.UpsertCareer(identityHash, restored);

            if (accountGuid != Guid.Empty && _characterStatePushBroker != null)
            {
                try { _characterStatePushBroker.Enqueue(accountGuid, CharacterStatePushPaths.Wallet | CharacterStatePushPaths.Inventory | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries); }
                catch { }
            }

            statusCode = 200;
            payload = new { ok = true, accountId = identityHash, slotIndex = slotIndex, restored = name.Trim(), career = restored.ToDictionary() };
        }

        private void HandleCareerPreset(string identityHash, int slotIndex, string bodyText, out int statusCode, out object payload)
        {
            var body = ParseJsonDict(bodyText);
            if (body == null)
            {
                statusCode = 400;
                payload = new { ok = false, error = "invalid_json", message = "Expected JSON object body with 'preset'." };
                return;
            }

            Guid accountGuid;
            if (!CheckAccountNotInMission(identityHash, body, out accountGuid, out statusCode, out payload))
            {
                return;
            }

            var preset = (GetString(body, "preset") ?? string.Empty).Trim().ToLowerInvariant();
            var slot = _userStore.GetOrCreateCareer(identityHash, slotIndex, true);
            if (slot == null)
            {
                statusCode = 500;
                payload = new { ok = false, error = "career_load_failed", message = "Failed to load career slot." };
                return;
            }

            var staticIndex = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);

            if (preset == "fresh_lv1")
            {
                slot.Karma = 0;
                slot.SpentKarma = 0;
                slot.Nuyen = 0;
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                SetEquippedWeapon(slot, true, "Automatics_IngramSmartgun_Tier_00", 0, -1);
                SetEquippedWeapon(slot, false, "Pistol_AresLightfire_Tier_00", 0, -1);
            }
            else if (preset == "max_all_skills")
            {
                slot.Karma = 500;
                slot.SpentKarma = 300;
                slot.Nuyen = 250000;
                UnlockAllSkillTrees(slot, staticIndex);
                UnlockAllCampaignMissions(slot, staticIndex);
            }
            else if (preset == "all_interactions")
            {
                slot.Karma = 100;
                slot.SpentKarma = 30;
                slot.Nuyen = 25000;
                if (slot.SkillTreeDefinitions == null)
                {
                    slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                }
                MergeTreeSkills(slot, "MindSkillTree", new string[]
                {
                    "MindLevelSkill_0_1", "MindLevelSkill_0_2",
                    "MindLevelSkill_2_1", "MindLevelSkill_2_2",
                    "MindLevelSkill_4_1", "MindLevelSkill_4_2",
                });
                MergeTreeSkills(slot, "BodySkillTree", new string[]
                {
                    "BodyLevelSkill_0_1", "BodyLevelSkill_0_2",
                    "BodyLevelSkill_2_1", "BodyLevelSkill_2_2",
                    "BodyLevelSkill_4_1", "BodyLevelSkill_4_2",
                });
            }
            else
            {
                statusCode = 400;
                payload = new { ok = false, error = "unknown_preset", message = "Supported presets: fresh_lv1, all_interactions, max_all_skills." };
                return;
            }

            var overrides = GetDict(body, "overrides");
            if (overrides != null)
            {
                string err;
                if (!ApplyCareerPatch(slot, overrides, false, out err))
                {
                    statusCode = 400;
                    payload = new { ok = false, error = "validation_failed", message = err };
                    return;
                }
            }

            _userStore.UpsertCareer(identityHash, slot);
            if (accountGuid != Guid.Empty && _characterStatePushBroker != null)
            {
                try { _characterStatePushBroker.Enqueue(accountGuid, CharacterStatePushPaths.Wallet | CharacterStatePushPaths.Inventory | CharacterStatePushPaths.MetaSnapshot | CharacterStatePushPaths.CareerSummaries); }
                catch { }
            }

            statusCode = 200;
            payload = new { ok = true, accountId = identityHash, slotIndex = slotIndex, preset = preset, career = slot.ToDictionary() };
        }

        private bool ApplyCareerPatch(CareerSlot slot, IDictionary body, bool isFullReplace, out string error)
        {
            error = null;
            var staticIndex = MetagameplayStaticDataIndex.Load(_options != null ? _options.StaticDataDir : null);

            if (isFullReplace)
            {
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                slot.ActiveUnlocks = new List<string>();
            }

            var characterName = GetString(body, "characterName");
            if (!IsNullOrWhiteSpace(characterName))
            {
                slot.CharacterName = characterName.Trim();
            }

            if (body.Contains("bodytype") && body["bodytype"] != null)
            {
                slot.Bodytype = Convert.ToUInt64(body["bodytype"], CultureInfo.InvariantCulture);
            }
            if (body.Contains("skinTextureIndex") && body["skinTextureIndex"] != null)
            {
                slot.SkinTextureIndex = Convert.ToInt32(body["skinTextureIndex"], CultureInfo.InvariantCulture);
            }
            if (body.Contains("backgroundStory") && body["backgroundStory"] != null)
            {
                slot.BackgroundStory = Convert.ToUInt64(body["backgroundStory"], CultureInfo.InvariantCulture);
            }
            if (body.Contains("karma") && body["karma"] != null)
            {
                slot.Karma = Convert.ToInt32(body["karma"], CultureInfo.InvariantCulture);
            }
            if (body.Contains("spentKarma") && body["spentKarma"] != null)
            {
                slot.SpentKarma = Convert.ToInt32(body["spentKarma"], CultureInfo.InvariantCulture);
            }
            if (body.Contains("nuyen") && body["nuyen"] != null)
            {
                slot.Nuyen = Convert.ToInt32(body["nuyen"], CultureInfo.InvariantCulture);
            }

            var loadout = GetDict(body, "loadout");
            if (loadout != null)
            {
                if (!ApplyLoadoutPatch(slot, loadout, staticIndex, out error))
                {
                    return false;
                }
            }

            var skills = GetDict(body, "skills");
            if (skills != null)
            {
                if (!ApplySkillsPatch(slot, skills, staticIndex, out error))
                {
                    return false;
                }
            }

            var inventory = GetDict(body, "inventory");
            if (inventory != null)
            {
                if (GetBool(inventory, "clearOthers", false))
                {
                    slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                }
                var grant = GetDict(inventory, "grant");
                if (grant != null)
                {
                    if (slot.ItemPossessions == null)
                    {
                        slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    }
                    foreach (DictionaryEntry entry in grant)
                    {
                        var itemId = entry.Key as string;
                        if (IsNullOrWhiteSpace(itemId))
                        {
                            continue;
                        }
                        if (staticIndex.ItemDefinitions.Count > 0 && !staticIndex.ItemDefinitions.ContainsKey(itemId))
                        {
                            error = "Unknown itemId in inventory.grant: " + itemId;
                            return false;
                        }
                        var amt = Convert.ToInt32(entry.Value, CultureInfo.InvariantCulture);
                        var packedKey = itemId + "|0|-1";
                        if (amt <= 0)
                        {
                            slot.ItemPossessions.Remove(packedKey);
                        }
                        else
                        {
                            slot.ItemPossessions[packedKey] = amt;
                        }
                    }
                }
            }

            var progression = GetDict(body, "progression");
            if (progression != null)
            {
                ApplyProgressionPatch(slot, progression, staticIndex);
            }

            slot.IsOccupied = true;
            slot.PendingPersistenceCreation = false;
            return true;
        }

        private static bool ApplyLoadoutPatch(CareerSlot slot, IDictionary loadout, MetagameplayStaticDataIndex staticIndex, out string error)
        {
            error = null;
            if (slot.EquippedItems == null)
            {
                slot.EquippedItems = new Dictionary<string, CareerSlot.EquippedSlotState>(StringComparer.OrdinalIgnoreCase);
            }

            string itemId;
            int quality;
            int flavour;
            if (TryReadLoadoutItem(loadout, "primaryWeapon", out itemId, out quality, out flavour))
            {
                if (!ValidateItemId(staticIndex, itemId, out error)) return false;
                SetEquippedWeapon(slot, true, itemId, quality, flavour);
            }

            if (TryReadLoadoutItem(loadout, "secondaryWeapon", out itemId, out quality, out flavour))
            {
                if (!ValidateItemId(staticIndex, itemId, out error)) return false;
                SetEquippedWeapon(slot, false, itemId, quality, flavour);
            }

            if (TryReadLoadoutItem(loadout, "armor", out itemId, out quality, out flavour))
            {
                if (!ValidateItemId(staticIndex, itemId, out error)) return false;
                slot.ArmorItemId = itemId;
                slot.ArmorQuality = quality;
                slot.ArmorFlavour = flavour;
                if (slot.ArmorInventoryKey < 0) slot.ArmorInventoryKey = 2;
                EnsurePossession(slot, itemId, quality, flavour, 1);
            }

            var equippedSlots = GetDict(loadout, "equippedSlots");
            if (equippedSlots != null)
            {
                var nextInvKey = FindNextFreeInventoryKey(slot);
                foreach (DictionaryEntry kvp in equippedSlots)
                {
                    var slotKey = kvp.Key as string;
                    if (IsNullOrWhiteSpace(slotKey))
                    {
                        continue;
                    }
                    if (kvp.Value == null)
                    {
                        slot.EquippedItems.Remove(slotKey);
                        continue;
                    }
                    string eqItemId;
                    int eqQ = 0;
                    int eqF = -1;
                    var eqDict = kvp.Value as IDictionary;
                    if (eqDict != null)
                    {
                        eqItemId = GetString(eqDict, "itemId");
                        eqQ = GetInt(eqDict, "quality", 0);
                        eqF = GetInt(eqDict, "flavour", -1);
                    }
                    else
                    {
                        eqItemId = kvp.Value as string;
                    }

                    if (IsNullOrWhiteSpace(eqItemId))
                    {
                        slot.EquippedItems.Remove(slotKey);
                        continue;
                    }
                    if (!ValidateItemId(staticIndex, eqItemId, out error)) return false;

                    slot.EquippedItems[slotKey] = CareerSlot.EquippedSlotState.Create(eqItemId, nextInvKey++, eqQ, eqF);
                    EnsurePossession(slot, eqItemId, eqQ, eqF, 1);
                }
            }

            return true;
        }

        private static bool ApplySkillsPatch(CareerSlot slot, IDictionary skills, MetagameplayStaticDataIndex staticIndex, out string error)
        {
            error = null;
            if (GetBool(skills, "replace", false) || slot.SkillTreeDefinitions == null)
            {
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }

            if (GetBool(skills, "unlockAllSkills", false))
            {
                UnlockAllSkillTrees(slot, staticIndex);
            }

            var unlockTrees = GetArray(skills, "unlockAllInTrees");
            if (unlockTrees != null)
            {
                for (var i = 0; i < unlockTrees.Length; i++)
                {
                    var treeName = unlockTrees[i] as string;
                    if (IsNullOrWhiteSpace(treeName))
                    {
                        continue;
                    }
                    MetagameplayStaticDataIndex.SkillTreeInfo treeInfo;
                    if (!staticIndex.SkillTrees.TryGetValue(treeName, out treeInfo) || treeInfo == null)
                    {
                        error = "Unknown skill tree: " + treeName;
                        return false;
                    }
                    var allTreeSkills = new List<string>();
                    for (var lvIdx = 0; lvIdx < treeInfo.SkillLevels.Count; lvIdx++)
                    {
                        var lv = treeInfo.SkillLevels[lvIdx];
                        if (lv == null || lv.Skills == null) continue;
                        for (var sIdx = 0; sIdx < lv.Skills.Count; sIdx++)
                        {
                            if (lv.Skills[sIdx] != null && !IsNullOrWhiteSpace(lv.Skills[sIdx].TechnicalName))
                            {
                                allTreeSkills.Add(lv.Skills[sIdx].TechnicalName);
                            }
                        }
                    }
                    MergeTreeSkills(slot, treeName, allTreeSkills.ToArray());
                }
            }

            var byTree = GetDict(skills, "byTree");
            if (byTree != null)
            {
                foreach (DictionaryEntry entry in byTree)
                {
                    var treeName = entry.Key as string;
                    if (IsNullOrWhiteSpace(treeName))
                    {
                        continue;
                    }
                    if (staticIndex.SkillTrees.Count > 0 && !staticIndex.SkillTrees.ContainsKey(treeName))
                    {
                        error = "Unknown skill tree in skills.byTree: " + treeName;
                        return false;
                    }
                    var arr = CoerceArray(entry.Value);
                    var list = new List<string>();
                    if (arr != null)
                    {
                        for (var i = 0; i < arr.Length; i++)
                        {
                            var skName = arr[i] as string;
                            if (IsNullOrWhiteSpace(skName)) continue;
                            MetagameplayStaticDataIndex.SkillTreeInfo st;
                            int lvIdx;
                            MetagameplayStaticDataIndex.SkillInfo skInfo;
                            if (staticIndex.SkillTrees.Count > 0 && !staticIndex.TryGetSkill(treeName, skName, out st, out lvIdx, out skInfo))
                            {
                                error = "Unknown skill '" + skName + "' in tree '" + treeName + "'.";
                                return false;
                            }
                            list.Add(skName);
                        }
                    }
                    MergeTreeSkills(slot, treeName, list.ToArray());
                }
            }

            return true;
        }

        private static void ApplyProgressionPatch(CareerSlot slot, IDictionary progression, MetagameplayStaticDataIndex staticIndex)
        {
            if (slot.MainCampaignMissionStates == null)
            {
                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (slot.ActiveUnlocks == null)
            {
                slot.ActiveUnlocks = new List<string>();
            }

            if (GetBool(progression, "unlockAllMissions", false))
            {
                UnlockAllCampaignMissions(slot, staticIndex);
            }

            var unlockMission = GetString(progression, "unlockMission");
            if (!IsNullOrWhiteSpace(unlockMission))
            {
                UnlockUpToMission(slot, staticIndex, unlockMission.Trim());
            }

            var hubId = GetString(progression, "hubId");
            if (!IsNullOrWhiteSpace(hubId))
            {
                slot.HubId = hubId.Trim();
            }

            if (progression.Contains("mainCampaignCurrentChapter") && progression["mainCampaignCurrentChapter"] != null)
            {
                slot.MainCampaignCurrentChapter = Convert.ToInt32(progression["mainCampaignCurrentChapter"], CultureInfo.InvariantCulture);
            }

            var missionStates = GetDict(progression, "missionStates");
            if (missionStates != null)
            {
                foreach (DictionaryEntry kvp in missionStates)
                {
                    var mName = kvp.Key as string;
                    var mState = kvp.Value as string;
                    if (!IsNullOrWhiteSpace(mName) && !IsNullOrWhiteSpace(mState))
                    {
                        slot.MainCampaignMissionStates[mName] = mState;
                    }
                }
            }

            var activeUnlocks = GetArray(progression, "activeUnlocks");
            if (activeUnlocks != null)
            {
                for (var i = 0; i < activeUnlocks.Length; i++)
                {
                    var u = activeUnlocks[i] as string;
                    if (!IsNullOrWhiteSpace(u) && !slot.ActiveUnlocks.Contains(u))
                    {
                        slot.ActiveUnlocks.Add(u);
                    }
                }
            }
        }

        private static void UnlockUpToMission(CareerSlot slot, MetagameplayStaticDataIndex staticIndex, string missionName)
        {
            if (staticIndex == null || staticIndex.Storylines == null)
            {
                slot.MainCampaignMissionStates[missionName] = "Available";
                return;
            }

            MetagameplayStaticDataIndex.StorylineInfo main;
            if (!staticIndex.Storylines.TryGetValue("Main Campaign", out main) || main == null || main.Chapters == null)
            {
                slot.MainCampaignMissionStates[missionName] = "Available";
                return;
            }

            var targetChapterIdx = -1;
            for (var i = 0; i < main.Chapters.Count; i++)
            {
                var ch = main.Chapters[i];
                if (ch == null) continue;
                if (ContainsIgnoreCase(ch.RequiredMissions, missionName) || ContainsIgnoreCase(ch.SideMissions, missionName))
                {
                    targetChapterIdx = ch.Index;
                    if (!IsNullOrWhiteSpace(ch.Hub))
                    {
                        slot.HubId = ch.Hub;
                    }
                    break;
                }
            }

            if (targetChapterIdx >= 0)
            {
                slot.MainCampaignCurrentChapter = targetChapterIdx;
                for (var i = 0; i < main.Chapters.Count; i++)
                {
                    var ch = main.Chapters[i];
                    if (ch == null || ch.Index >= targetChapterIdx) continue;
                    if (ch.RequiredMissions != null)
                    {
                        for (var j = 0; j < ch.RequiredMissions.Count; j++)
                        {
                            slot.MainCampaignMissionStates[ch.RequiredMissions[j]] = "Completed";
                        }
                    }
                    if (ch.RequiredUnlocks != null)
                    {
                        for (var j = 0; j < ch.RequiredUnlocks.Count; j++)
                        {
                            if (!slot.ActiveUnlocks.Contains(ch.RequiredUnlocks[j]))
                            {
                                slot.ActiveUnlocks.Add(ch.RequiredUnlocks[j]);
                            }
                        }
                    }
                }
            }

            slot.MainCampaignMissionStates[missionName] = "Available";
        }

        private static void UnlockAllCampaignMissions(CareerSlot slot, MetagameplayStaticDataIndex staticIndex)
        {
            if (staticIndex == null || staticIndex.Storylines == null)
            {
                return;
            }
            if (slot.MainCampaignMissionStates == null)
            {
                slot.MainCampaignMissionStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            if (slot.ActiveUnlocks == null)
            {
                slot.ActiveUnlocks = new List<string>();
            }
            foreach (var kvp in staticIndex.Storylines)
            {
                var st = kvp.Value;
                if (st == null || st.Chapters == null) continue;
                for (var i = 0; i < st.Chapters.Count; i++)
                {
                    var ch = st.Chapters[i];
                    if (ch == null) continue;
                    if (ch.RequiredMissions != null)
                    {
                        for (var j = 0; j < ch.RequiredMissions.Count; j++)
                        {
                            slot.MainCampaignMissionStates[ch.RequiredMissions[j]] = "Available";
                        }
                    }
                    if (ch.SideMissions != null)
                    {
                        for (var j = 0; j < ch.SideMissions.Count; j++)
                        {
                            slot.MainCampaignMissionStates[ch.SideMissions[j]] = "Available";
                        }
                    }
                }
            }
        }

        private static void UnlockAllSkillTrees(CareerSlot slot, MetagameplayStaticDataIndex staticIndex)
        {
            if (staticIndex == null || staticIndex.SkillTrees == null)
            {
                return;
            }
            if (slot.SkillTreeDefinitions == null)
            {
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }
            foreach (var kvp in staticIndex.SkillTrees)
            {
                var tree = kvp.Value;
                if (tree == null || tree.SkillLevels == null) continue;
                var list = new List<string>();
                for (var i = 0; i < tree.SkillLevels.Count; i++)
                {
                    var lv = tree.SkillLevels[i];
                    if (lv == null || lv.Skills == null) continue;
                    for (var j = 0; j < lv.Skills.Count; j++)
                    {
                        if (lv.Skills[j] != null && !IsNullOrWhiteSpace(lv.Skills[j].TechnicalName))
                        {
                            list.Add(lv.Skills[j].TechnicalName);
                        }
                    }
                }
                slot.SkillTreeDefinitions[kvp.Key] = list.ToArray();
            }
        }

        private static void MergeTreeSkills(CareerSlot slot, string treeName, string[] skillsToAdd)
        {
            if (slot.SkillTreeDefinitions == null)
            {
                slot.SkillTreeDefinitions = new Dictionary<string, string[]>(StringComparer.Ordinal);
            }
            var set = new HashSet<string>(StringComparer.Ordinal);
            string[] existing;
            if (slot.SkillTreeDefinitions.TryGetValue(treeName, out existing) && existing != null)
            {
                for (var i = 0; i < existing.Length; i++)
                {
                    if (!IsNullOrWhiteSpace(existing[i])) set.Add(existing[i]);
                }
            }
            if (skillsToAdd != null)
            {
                for (var i = 0; i < skillsToAdd.Length; i++)
                {
                    if (!IsNullOrWhiteSpace(skillsToAdd[i])) set.Add(skillsToAdd[i]);
                }
            }
            var merged = new string[set.Count];
            set.CopyTo(merged);
            slot.SkillTreeDefinitions[treeName] = merged;
        }

        private static void SetEquippedWeapon(CareerSlot slot, bool primary, string itemId, int quality, int flavour)
        {
            if (primary)
            {
                slot.PrimaryWeaponItemId = itemId;
                slot.PrimaryWeaponQuality = quality;
                slot.PrimaryWeaponFlavour = flavour;
                if (slot.PrimaryWeaponInventoryKey < 0) slot.PrimaryWeaponInventoryKey = 0;
            }
            else
            {
                slot.SecondaryWeaponItemId = itemId;
                slot.SecondaryWeaponQuality = quality;
                slot.SecondaryWeaponFlavour = flavour;
                if (slot.SecondaryWeaponInventoryKey <= 0) slot.SecondaryWeaponInventoryKey = 1;
            }
            EnsurePossession(slot, itemId, quality, flavour, 1);
        }

        private static void EnsurePossession(CareerSlot slot, string itemId, int quality, int flavour, int minAmount)
        {
            if (slot == null || IsNullOrWhiteSpace(itemId))
            {
                return;
            }
            if (slot.ItemPossessions == null)
            {
                slot.ItemPossessions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            var packed = itemId + "|" + quality.ToString(CultureInfo.InvariantCulture) + "|" + flavour.ToString(CultureInfo.InvariantCulture);
            int cur;
            if (!slot.ItemPossessions.TryGetValue(packed, out cur) || cur < minAmount)
            {
                slot.ItemPossessions[packed] = minAmount;
            }
        }

        private static int FindNextFreeInventoryKey(CareerSlot slot)
        {
            var maxKey = 10;
            if (slot != null && slot.EquippedItems != null)
            {
                foreach (var kvp in slot.EquippedItems)
                {
                    if (kvp.Value != null && kvp.Value.InventoryKey >= maxKey)
                    {
                        maxKey = kvp.Value.InventoryKey + 1;
                    }
                }
            }
            return maxKey;
        }

        private static bool TryReadLoadoutItem(IDictionary loadout, string key, out string itemId, out int quality, out int flavour)
        {
            itemId = null;
            quality = 0;
            flavour = -1;
            if (!loadout.Contains(key) || loadout[key] == null)
            {
                return false;
            }
            var asDict = loadout[key] as IDictionary;
            if (asDict != null)
            {
                itemId = GetString(asDict, "itemId");
                quality = GetInt(asDict, "quality", 0);
                flavour = GetInt(asDict, "flavour", -1);
                return !IsNullOrWhiteSpace(itemId);
            }
            itemId = loadout[key] as string;
            return !IsNullOrWhiteSpace(itemId);
        }

        private static bool ValidateItemId(MetagameplayStaticDataIndex staticIndex, string itemId, out string error)
        {
            error = null;
            if (staticIndex != null && staticIndex.ItemDefinitions != null && staticIndex.ItemDefinitions.Count > 0)
            {
                if (!staticIndex.ItemDefinitions.ContainsKey(itemId))
                {
                    error = "Unknown itemId: " + itemId;
                    return false;
                }
            }
            return true;
        }

        private string ResolveAccountIdentity(string rawAccountId)
        {
            if (_userStore == null)
            {
                return null;
            }
            if (string.Equals(rawAccountId, "default", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawAccountId, "latest", StringComparison.OrdinalIgnoreCase))
            {
                var ids = _userStore.ListAccountIdentitiesForDebug();
                if (ids != null && ids.Length > 0)
                {
                    return ids[ids.Length - 1];
                }
                return _userStore.GetOrCreateIdentityHash();
            }
            Guid g;
            if (TryParseGuid(rawAccountId, out g) && g != Guid.Empty)
            {
                return g.ToString("D");
            }
            return null;
        }

        private static bool ContainsIgnoreCase(List<string> list, string target)
        {
            if (list == null) return false;
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class DebugHttpRequest
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> QueryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string BodyText = string.Empty;
        }

        private const int MaxHeaderBytes = 1024 * 1024;
        private const int MaxBodyBytes = 4 * 1024 * 1024;

        private static DebugHttpRequest ReadRequest(NetworkStream stream)
        {
            var raw = new MemoryStream();
            var buf = new byte[4096];
            var headerEnd = -1;
            var contentLength = 0;

            while (headerEnd < 0)
            {
                var read = stream.Read(buf, 0, buf.Length);
                if (read <= 0)
                {
                    if (raw.Length == 0) return null;
                    break;
                }
                raw.Write(buf, 0, read);
                var data = raw.ToArray();
                headerEnd = FindHeaderEnd(data);
                if (raw.Length > MaxHeaderBytes)
                {
                    throw new InvalidOperationException("Request headers too large.");
                }
            }

            if (headerEnd < 0)
            {
                return null;
            }

            var initialBytes = raw.ToArray();
            var headerText = Encoding.ASCII.GetString(initialBytes, 0, headerEnd);
            var lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var reqLine = lines[0].Split(' ');
            if (reqLine.Length < 2)
            {
                throw new InvalidOperationException("Malformed HTTP request line.");
            }

            var req = new DebugHttpRequest();
            req.Method = reqLine[0].Trim();
            var target = reqLine[1].Trim();
            var qIdx = target.IndexOf('?');
            if (qIdx >= 0)
            {
                req.Path = target.Substring(0, qIdx);
                ParseQuery(target.Substring(qIdx + 1), req.QueryParams);
            }
            else
            {
                req.Path = target;
            }

            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                var hName = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                var hVal = lines[i].Substring(colon + 1).Trim();
                req.Headers[hName] = hVal;
                if (hName == "content-length")
                {
                    if (!int.TryParse(hVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                    {
                        throw new InvalidOperationException("Invalid Content-Length header.");
                    }
                    if (contentLength > MaxBodyBytes)
                    {
                        throw new InvalidOperationException("Request body too large.");
                    }
                }
            }

            var bodyStart = headerEnd + 4;
            var bodyStream = new MemoryStream();
            if (contentLength > 0 && initialBytes.Length > bodyStart)
            {
                var initialBodyLen = Math.Min(initialBytes.Length - bodyStart, contentLength);
                bodyStream.Write(initialBytes, bodyStart, initialBodyLen);
            }

            while (bodyStream.Length < contentLength)
            {
                var remaining = (int)Math.Min(buf.Length, contentLength - bodyStream.Length);
                var read = stream.Read(buf, 0, remaining);
                if (read <= 0) break;
                bodyStream.Write(buf, 0, read);
            }

            if (bodyStream.Length > 0)
            {
                req.BodyText = Encoding.UTF8.GetString(bodyStream.ToArray());
            }

            return req;
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (var i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool TryUnescapeDataString(string input, out string unescaped)
        {
            if (string.IsNullOrEmpty(input))
            {
                unescaped = input ?? string.Empty;
                return true;
            }

            try
            {
                unescaped = Uri.UnescapeDataString(input);
                return true;
            }
            catch (UriFormatException)
            {
                unescaped = null;
                return false;
            }
        }

        private static void ParseQuery(string qs, Dictionary<string, string> dest)
        {
            if (IsNullOrWhiteSpace(qs)) return;
            var pairs = qs.Split('&');
            for (var i = 0; i < pairs.Length; i++)
            {
                var eq = pairs[i].IndexOf('=');
                if (eq > 0)
                {
                    string key;
                    string val;
                    if (!TryUnescapeDataString(pairs[i].Substring(0, eq), out key)
                        || !TryUnescapeDataString(pairs[i].Substring(eq + 1), out val))
                    {
                        throw new InvalidOperationException("Malformed percent-encoding in query string.");
                    }
                    dest[key] = val;
                }
                else if (pairs[i].Length > 0)
                {
                    string key;
                    if (!TryUnescapeDataString(pairs[i], out key))
                    {
                        throw new InvalidOperationException("Malformed percent-encoding in query string.");
                    }
                    dest[key] = "true";
                }
            }
        }

        private static void WriteJsonResponse(NetworkStream stream, int statusCode, object payload)
        {
            var json = Json.Serialize(payload);
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            var reason = StatusReason(statusCode);
            var headers = "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n"
                + "Content-Type: application/json; charset=utf-8\r\n"
                + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
                + "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static string StatusReason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                default: return "Internal Server Error";
            }
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 32 * 1024 * 1024;
            return ser;
        }

        private static IDictionary ParseJsonDict(string text)
        {
            if (IsNullOrWhiteSpace(text)) return null;
            try { return Json.DeserializeObject(text) as IDictionary; }
            catch { return null; }
        }

        private static IDictionary GetDict(IDictionary d, string key)
        {
            if (d == null || !d.Contains(key)) return null;
            return d[key] as IDictionary;
        }

        private static object[] GetArray(IDictionary d, string key)
        {
            if (d == null || !d.Contains(key)) return null;
            return CoerceArray(d[key]);
        }

        private static object[] CoerceArray(object value)
        {
            var arr = value as object[];
            if (arr != null) return arr;
            var list = value as ArrayList;
            if (list != null) return list.ToArray();
            return null;
        }

        private static string GetString(IDictionary d, string key)
        {
            if (d == null || !d.Contains(key) || d[key] == null) return null;
            return d[key] as string;
        }

        private static bool GetBool(IDictionary d, string key, bool fallback)
        {
            if (d == null || !d.Contains(key) || d[key] == null) return fallback;
            try { return Convert.ToBoolean(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static int GetInt(IDictionary d, string key, int fallback)
        {
            if (d == null || !d.Contains(key) || d[key] == null) return fallback;
            try { return Convert.ToInt32(d[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool GetQueryBool(Dictionary<string, string> query, string key, bool fallback)
        {
            string raw;
            if (query == null || !query.TryGetValue(key, out raw) || IsNullOrWhiteSpace(raw)) return fallback;
            var t = raw.Trim().ToLowerInvariant();
            if (t == "1" || t == "true" || t == "yes") return true;
            if (t == "0" || t == "false" || t == "no") return false;
            return fallback;
        }

        private static int GetQueryInt(Dictionary<string, string> query, string key, int fallback)
        {
            string raw;
            int val;
            if (query != null && query.TryGetValue(key, out raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
            {
                return val;
            }
            return fallback;
        }

        private static bool TryParseGuid(string raw, out Guid g)
        {
            g = Guid.Empty;
            if (IsNullOrWhiteSpace(raw)) return false;
            try { g = new Guid(raw.Trim()); return true; }
            catch { return false; }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
